using System.Text.RegularExpressions;
using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Mapping.Dpm20;

/// <summary>
/// Loads the six DPM 2.0 dictionary tables: <c>mDomain</c>, <c>mDomainUnion</c>, <c>mMember</c>,
/// <c>mHierarchy</c> and <c>mMetric</c>, <c>mDimension</c>. With this the dictionary is complete.
///
/// This is a NEW pipeline, PARALLEL to the DPM 1.0
/// <see cref="EbaDpm.Converter.Core.Mapping.DictionaryLoader"/>: same stylistic SHAPE (walk the
/// source, translate, write), its own business rules — starting with the fact that here the metric
/// domain (<c>MET</c>) and its members are identified by ROLE, not by a source column or a fixed
/// <c>DomainID</c> (there is no <c>Metric</c> as a separate table).
///
/// It runs after <see cref="Dpm20SkeletonLoader.Load"/>, on the same connection.
///
/// No release filter: the dictionary is shared between taxonomies and is not pruned by release
/// here, as in DPM 1.0. Measured: the counts that match the reference come from
/// <c>ItemCategory</c>, <c>SuperCategoryComposition</c> and <c>SubCategory</c> WITHOUT applying
/// <c>StartReleaseID</c>/<c>EndReleaseID</c>. The only exception is
/// <c>PropertyCategory.StartReleaseID</c>, which IS read — but not to filter: because it IS the
/// release suffix of <c>mDimension.DimensionXBRLCode</c>.
///
/// <b>43 properties are metric AND dimension at the same time.</b> No
/// <c>if metric else dimension</c>: <see cref="LoadMetrics"/> and <see cref="LoadDimensions"/> are
/// evaluated separately and the same property can produce a row in both tables.
/// </summary>
public static class Dpm20DictionaryLoader
{
    /// <summary>Count of rows written per table, for the CLI report.</summary>
    public sealed record Result(
        int DomainRows,
        int DomainUnionRows,
        int MemberRows,
        int HierarchyRows,
        int MetricRows,
        int DimensionRows,
        IReadOnlyDictionary<int, (int HierarchyId, string HierarchyCode, int DomainId)> HierarchyBySubCategoryId);

    // ------------------------------------------------------------------
    // DataType translation, from the one- or two-letter source code to the destination name
    // (mMetric.DataType; and, for the _NA → INT_NA/STR_NA split of mDimension, WITHOUT translation
    // — the raw code 'i'/'s' is used). dt, u, o of the source have no known translation: if any
    // metric property uses them, they are collected in UnresolvedCases and reported — nothing is
    // invented.
    //
    // The source 's'/'es' do not decide anything by themselves — measured, 's' goes to BOTH
    // destination families (173 to NotEmptyString, 137 to String) and 'es' too (15 to String, 1 to
    // NotEmptyString). The real discriminator is the property NAMING LAW: if the code matches
    // <c>^[a-z][a-z][0-9]+$</c> (e.g. <c>si1036</c>) → <c>String</c>; otherwise (e.g. <c>qANS</c>)
    // → <c>NotEmptyString</c>. Zero failures over 2,100 measured against the reference. A first
    // version ('s'→NotEmptyString, 'es'→String fixed) was an unmeasured inference from the NAME of
    // the source column — the pattern of reading the source with the destination's vocabulary,
    // which this project avoids. See <see cref="ResolveDataType"/>.
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, string> DataTypeTranslation = new(StringComparer.Ordinal)
    {
        ["i"] = "Integer",
        ["r"] = "Decimal",
        ["b"] = "Boolean",
        ["t"] = "BooleanTrue",
        ["d"] = "Date",
        ["e"] = "Enumeration/Code",
        ["m"] = "Monetary",
        ["p"] = "Percent",
        // 's' and 'es' are NOT here on purpose: they are resolved in ResolveDataType by the naming
        // law, not by the raw source code.
    };

    /// <summary>
    /// Translates the raw source <c>DataType.Code</c> to the destination name for a <c>mMetric</c>
    /// row. 's' and 'es' (string) are resolved by the property naming law: <c>String</c> if the
    /// code matches <c>^[a-z][a-z][0-9]+$</c>, <c>NotEmptyString</c> otherwise. The rest comes out
    /// of <see cref="DataTypeTranslation"/> as is. Returns <c>null</c> if there is no known
    /// translation (only i, r, s, b, t, d, e, m, p, es are covered).
    /// </summary>
    private static string? ResolveDataType(string rawCode, string propertyCode)
    {
        if (rawCode is "s" or "es")
        {
            return MetricNameLawPattern.IsMatch(propertyCode) ? "String" : "NotEmptyString";
        }

        return DataTypeTranslation.GetValueOrDefault(rawCode);
    }

    // ------------------------------------------------------------------
    // mDomain — 146 from Category (excluding SE, _NA, _PR, _TE) + 4 synthesized (MET,
    // INT_NA, STR_NA, the "Open" sentinel).
    // ------------------------------------------------------------------

    private static readonly HashSet<string> ExcludedDomainCategoryCodes = new(StringComparer.Ordinal)
    {
        "SE", "_NA", "_PR", "_TE",
    };

    /// <summary>
    /// The <c>DomainID</c>/<c>MemberID</c> of the "Open" sentinel is NOT the next value of the
    /// counter — it is the LITERAL 9999, measured identical in two reference databases (domain and
    /// member "Open" are 9999 in both, with 389 and 89 open-axis categorisations pointing there
    /// respectively). Before this was fixed it was minted by counter (150 and 12,949) and the real
    /// 9999 was taken by an arbitrary member (<c>qx2366 Consumer loans</c>), so the 389 open-axis
    /// categorisations (<see cref="Dpm20AxisAndCellLoader"/>, which already wrote
    /// <c>MemberID=9999</c> taking the sentinel for granted) pointed to a real member. Same value
    /// as <c>MetDimensionId</c>/<c>OpenAxisMemberSentinel</c> of
    /// <see cref="Dpm20AxisAndCellLoader"/> — not a coincidence, it is the same 9999 measured in
    /// the reference.
    /// </summary>
    private const int OpenSentinelId = 9999;

    /// <summary>What the rest of the <c>Load*</c> methods need from <see cref="LoadDomains"/>.</summary>
    private sealed record DomainLoadResult(
        int RowsWritten,
        IReadOnlyDictionary<int, int> DomainIdByCategoryId,
        IReadOnlyDictionary<int, string> DomainCodeByCategoryId,
        IReadOnlyDictionary<int, bool> IsTypedDomainByDomainId,
        int MetDomainId,
        int SentinelDomainId,
        int PrCategoryId,
        int TeCategoryId,
        int NaCategoryId,
        int IntNaDomainId,
        int StrNaDomainId);

    private static DomainLoadResult LoadDomains(SqliteConnection destination, IReadOnlyList<Dpm20CategoryRow> categories)
    {
        using var writer = new SqliteBatchWriter(
            destination,
            "mDomain",
            ["DomainID", "DomainCode", "DomainLabel", "DomainDescription", "DomainXBRLCode", "DataType", "IsTypedDomain", "IsNillable", "ConceptID"]);

        var domainIdByCategoryId = new Dictionary<int, int>();
        var domainCodeByCategoryId = new Dictionary<int, string>();
        var isTypedDomainByDomainId = new Dictionary<int, bool>();
        var domainId = 1;

        int? prCategoryId = null;
        int? teCategoryId = null;
        int? naCategoryId = null;

        foreach (var category in categories.OrderBy(c => c.CategoryId))
        {
            if (string.Equals(category.Code, "_PR", StringComparison.Ordinal))
            {
                prCategoryId = category.CategoryId;
            }

            if (string.Equals(category.Code, "_TE", StringComparison.Ordinal))
            {
                teCategoryId = category.CategoryId;
            }

            if (string.Equals(category.Code, "_NA", StringComparison.Ordinal))
            {
                naCategoryId = category.CategoryId;
            }

            if (ExcludedDomainCategoryCodes.Contains(category.Code))
            {
                continue; // SE, _NA, _PR, _TE: produce no row in mDomain.
            }

            if (domainId == OpenSentinelId)
            {
                // 9999 is reserved for the "Open" sentinel — with 150 domains in total the counter
                // never reaches it today; if the source grew enough, it stops here instead of
                // letting a real domain silently occupy 9999.
                throw new InvalidOperationException(
                    $"The mDomain counter has reached the reserved ID {OpenSentinelId} "
                    + "(\"Open\" sentinel, measured as a literal in the reference) at category "
                    + $"CategoryID={category.CategoryId} ('{category.Code}'). The source has too many "
                    + "domains for this fixed reservation; review the numbering before continuing.");
            }

            var isTypedDomain = !category.IsEnumerated; // IsTypedDomain <=> NOT IsEnumerated (17 = 17)
            var xbrlCode = (isTypedDomain ? "eba_typ:" : "eba_exp:") + category.Code;

            writer.AddRow(
                domainId,
                category.Code,
                category.Name,
                string.Empty, // DomainDescription: constant ''
                xbrlCode,
                null, // DataType: NULL even for the 17 typed ones
                isTypedDomain,
                false, // IsNillable: constant 0
                null); // ConceptID: NULL until the concept loader runs

            domainIdByCategoryId[category.CategoryId] = domainId;
            domainCodeByCategoryId[category.CategoryId] = category.Code;
            isTypedDomainByDomainId[domainId] = isTypedDomain;
            domainId++;
        }

        if (prCategoryId is null || teCategoryId is null || naCategoryId is null)
        {
            throw new InvalidOperationException(
                "Categories '_PR', '_TE' and/or '_NA' were not found in Category; without "
                + "them the metric domain cannot be identified, templates cannot be excluded from "
                + "mMember, and the _NA → INT_NA/STR_NA split of mDimension cannot be resolved.");
        }

        // The four synthesized ones, in the measured order: MET, INT_NA, STR_NA, the sentinel.
        // The sentinel no longer continues the counter — it is minted with the literal
        // OpenSentinelId (9999) (see the constant's comment); MET/INT_NA/STR_NA remain sequential,
        // with the same collision guard as the category loop above.
        if (domainId >= OpenSentinelId)
        {
            throw new InvalidOperationException(
                $"The mDomain counter has reached the reserved ID {OpenSentinelId} "
                + "(\"Open\" sentinel) before synthesizing MET/INT_NA/STR_NA. The source has too many "
                + "domains for this fixed reservation; review the numbering before continuing.");
        }

        var metDomainId = domainId;
        writer.AddRow(metDomainId, "MET", "Metric domain", string.Empty, "MET", null, false, false, null);
        isTypedDomainByDomainId[metDomainId] = false;
        domainId++;

        var intNaDomainId = domainId;
        writer.AddRow(
            intNaDomainId,
            "INT_NA",
            // Typo "dimensons" from the source, copied VERBATIM: it is not corrected.
            "fictive domain created for string type properties used as typed dimensons",
            string.Empty,
            "eba_typ:INT_NA",
            "Integer",
            true,
            false,
            null);
        isTypedDomainByDomainId[intNaDomainId] = true;
        domainId++;

        var strNaDomainId = domainId;
        writer.AddRow(
            strNaDomainId,
            "STR_NA",
            "fictive domain created for string type properties used as typed dimensons", // same typo, verbatim
            string.Empty,
            "eba_typ:STR_NA",
            "NotEmptyString",
            true,
            false,
            null);
        isTypedDomainByDomainId[strNaDomainId] = true;
        domainId++;

        // The "Open" sentinel is NO LONGER domainId (the next counter value) — it is the literal
        // 9999, measured identical in the two references (see the XML doc of OpenSentinelId).
        // domainId (150 today) is left unused on purpose: no real domain has that value.
        var sentinelDomainId = OpenSentinelId;
        writer.AddRow(sentinelDomainId, string.Empty, "Open", string.Empty, string.Empty, null, false, false, null);
        isTypedDomainByDomainId[sentinelDomainId] = false;

        return new DomainLoadResult(
            (int)writer.RowsWritten,
            domainIdByCategoryId,
            domainCodeByCategoryId,
            isTypedDomainByDomainId,
            metDomainId,
            sentinelDomainId,
            prCategoryId.Value,
            teCategoryId.Value,
            naCategoryId.Value,
            intNaDomainId,
            strNaDomainId);
    }

    // ------------------------------------------------------------------
    // mDomainUnion — direct translation of SuperCategoryComposition. The union is NOT
    // materialized: in 0 of the 41 unions are the destination members the sum of their components.
    // ------------------------------------------------------------------

    private static int LoadDomainUnions(
        SqliteConnection destination,
        IReadOnlyList<Dpm20SuperCategoryCompositionRow> compositions,
        IReadOnlyDictionary<int, int> domainIdByCategoryId)
    {
        using var writer = new SqliteBatchWriter(destination, "mDomainUnion", ["UnionDomainID", "UnitedDomainID"]);

        foreach (var composition in compositions)
        {
            if (!domainIdByCategoryId.TryGetValue(composition.SuperCategoryId, out var unionDomainId))
            {
                throw new InvalidOperationException(
                    $"SuperCategoryComposition.SuperCategoryID={composition.SuperCategoryId} has no "
                    + "matching row in mDomain; union categories are assumed to be normal domains.");
            }

            if (!domainIdByCategoryId.TryGetValue(composition.CategoryId, out var unitedDomainId))
            {
                throw new InvalidOperationException(
                    $"SuperCategoryComposition.CategoryID={composition.CategoryId} has no "
                    + "matching row in mDomain; united categories are assumed to be normal domains.");
            }

            writer.AddRow(unionDomainId, unitedDomainId);
        }

        return (int)writer.RowsWritten;
    }

    // ------------------------------------------------------------------
    // mMember — all of ItemCategory except _PR and _TE, plus the MET domain (which comes out of
    // _PR through metric identification), plus the "Open" sentinel.
    // ------------------------------------------------------------------

    /// <summary>
    /// The naming law of metric properties:
    /// <c>&lt;letter&gt;&lt;letter&gt;&lt;number&gt;</c>, WITHOUT restriction to a specific
    /// prefix. The list of 10 prefixes (<c>mi, ei, si, pi, md, ii, bi, di, ri, pd</c>) found in the
    /// original analysis is NOT the definition of the criterion: it is the SAMPLE with which it was
    /// validated that the first letter predicts the <c>DataType</c> (1,120 of 1,120). Restricting
    /// the regex to those 10 prefixes leaves out real cases of the source (measured: <c>id176</c>,
    /// <c>id518</c>, <c>id542</c>, <c>id543</c>, <c>rd401</c>, <c>ti761</c> — all six are in the
    /// <c>MET</c> of the reference, zero false positives with the general pattern). Third source of
    /// metric identification, complementary to
    /// <see cref="Dpm20AccessReader.ReadFactVariablePropertyIds"/> and
    /// <see cref="Dpm20AccessReader.ReadNonKeyHeaderPropertyIds"/>. It is used only as a complement
    /// to those two (if it contradicted the role, the role would win) — here, being a union of the
    /// three sources, it never contradicts, it only adds.
    ///
    /// SECOND USE: the same regex decides <c>mMetric.DataType</c> when the source carries
    /// <c>s</c>/<c>es</c> (string) — see <see cref="ResolveDataType"/>. Measured: the raw source
    /// code does NOT discriminate <c>String</c> from <c>NotEmptyString</c> (both go to both
    /// destination families); the property code does, without a single mix (0 failures over 2,100).
    /// </summary>
    private static readonly Regex MetricNameLawPattern = new(
        "^[a-z][a-z][0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>What <see cref="LoadMetrics"/> needs from <see cref="LoadMembers"/>: the members of the MET domain, IN THE SAME ORDER in which they were written.</summary>
    private sealed record MemberLoadResult(
        int RowsWritten,
        IReadOnlyList<(int MemberId, int PropertyId, string Code)> MetMembers);

    private static MemberLoadResult LoadMembers(
        SqliteConnection destination,
        IReadOnlyList<Dpm20ItemCategoryRow> itemCategories,
        IReadOnlyDictionary<int, string?> itemNameById,
        IReadOnlyDictionary<int, int> domainIdByCategoryId,
        IReadOnlyDictionary<int, string> domainCodeByCategoryId,
        int metDomainId,
        int sentinelDomainId,
        int prCategoryId,
        int teCategoryId,
        HashSet<int> factPropertyIds,
        HashSet<int> nonKeyHeaderPropertyIds)
    {
        using var writer = new SqliteBatchWriter(
            destination,
            "mMember",
            ["MemberID", "DomainID", "MemberCode", "MemberLabel", "MemberXBRLCode", "IsDefaultMember", "ConceptID"]);

        var memberId = 1;
        var metMembers = new List<(int MemberId, int PropertyId, string Code)>();

        // 9999 is reserved for the "Open" sentinel (see OpenSentinelId). With 12,949 members the
        // counter DOES reach it (before the fix it was taken by a real member, measured: qx2366
        // "Consumer loans") — that value is skipped for the member that would have got it, which
        // takes the next gap. The resulting range is 1..9998, 10000..12949 for the real members,
        // dense except for the reserved gap, and 9999 for "Open" (the rest of the numbering remains
        // dense and deterministic).
        int NextMemberId()
        {
            if (memberId == OpenSentinelId)
            {
                memberId++;
            }

            return memberId++;
        }

        foreach (var item in itemCategories)
        {
            if (item.CategoryId == teCategoryId)
            {
                continue; // _TE: templates as items, not members.
            }

            var label = itemNameById.TryGetValue(item.ItemId, out var name) ? name : null;

            if (item.CategoryId == prCategoryId)
            {
                // Role fact ∪ non-key header ∪ naming law — metric identification, closed and
                // measured (2,104/2,104 against the reference). It is used ONLY to decide membership
                // of the MET domain of mMember; mMetric (DataType, referenced hierarchy, FlowType…)
                // is handled separately and is not touched here.
                var isMetric = factPropertyIds.Contains(item.ItemId)
                    || nonKeyHeaderPropertyIds.Contains(item.ItemId)
                    || MetricNameLawPattern.IsMatch(item.Code);

                if (!isMetric)
                {
                    continue; // Not a metric: a pure dimension of _PR, handled in mDimension (it is not a partition).
                }

                var metricMemberId = NextMemberId();
                writer.AddRow(metricMemberId, metDomainId, item.Code, label, $"eba_met:{item.Code}", item.IsDefaultItem, null);
                metMembers.Add((metricMemberId, item.ItemId, item.Code));
                continue;
            }

            if (!domainIdByCategoryId.TryGetValue(item.CategoryId, out var domainId))
            {
                throw new InvalidOperationException(
                    $"ItemCategory.CategoryID={item.CategoryId} (ItemID={item.ItemId}, Code='{item.Code}') "
                    + "has no matching row in mDomain. It is assumed that only _PR and _TE are left "
                    + "out of mDomain among the categories with members.");
            }

            var domainCode = domainCodeByCategoryId[item.CategoryId];
            var normalMemberId = NextMemberId();
            writer.AddRow(normalMemberId, domainId, item.Code, label, $"eba_{domainCode}:{item.Code}", item.IsDefaultItem, null);
        }

        // The "Open" sentinel: MemberID = OpenSentinelId (9999) literal — the gap that
        // NextMemberId() reserved above —, of the sentinel domain, also 9999.
        writer.AddRow(OpenSentinelId, sentinelDomainId, string.Empty, "Open", null, false, null);

        return new MemberLoadResult((int)writer.RowsWritten, metMembers);
    }

    // ------------------------------------------------------------------
    // mHierarchy — SubCategory, 1 to 1. The 16 rows whose CategoryID is that of _PR (codes
    // AT*, DPM 1.0 nomenclature of the metric domain) are assigned to the MET domain:
    // without this mapping they would be orphaned, because _PR produces no row in mDomain.
    // ------------------------------------------------------------------

    /// <summary>
    /// What <see cref="LoadMetrics"/> needs from <see cref="LoadHierarchies"/>: for each source
    /// <c>SubCategoryID</c>, the <c>mHierarchy</c> row it ended up in. It also feeds
    /// <c>mOpenAxisValueRestriction</c> (in <see cref="Dpm20AxisAndCellLoader"/>): this is the ONLY
    /// <c>SubCategoryID → HierarchyID</c> correspondence that exists (1:1), so it is reused as is
    /// instead of re-deriving it by matching on <c>HierarchyCode</c> against the already written
    /// <c>mHierarchy</c> — the code is NOT a unique key there (up to 484 duplicate keys), and
    /// matching by code would be ambiguous.
    /// </summary>
    private sealed record HierarchyLoadResult(
        int RowsWritten,
        IReadOnlyDictionary<int, (int HierarchyId, string HierarchyCode, int DomainId)> BySubCategoryId);

    private static HierarchyLoadResult LoadHierarchies(
        SqliteConnection destination,
        IReadOnlyList<Dpm20SubCategoryRow> subCategories,
        IReadOnlyDictionary<int, int> domainIdByCategoryId,
        int metDomainId,
        int prCategoryId)
    {
        using var writer = new SqliteBatchWriter(
            destination,
            "mHierarchy",
            ["HierarchyID", "HierarchyCode", "HierarchyLabel", "DomainID", "HierarchyDescription", "ConceptID"]);

        var hierarchyId = 1;
        var bySubCategoryId = new Dictionary<int, (int HierarchyId, string HierarchyCode, int DomainId)>();

        foreach (var subCategory in subCategories)
        {
            int domainId;
            if (subCategory.CategoryId == prCategoryId)
            {
                domainId = metDomainId; // AT* — hierarchies of the metric domain.
            }
            else if (!domainIdByCategoryId.TryGetValue(subCategory.CategoryId, out domainId))
            {
                throw new InvalidOperationException(
                    $"SubCategory.CategoryID={subCategory.CategoryId} (SubCategoryID={subCategory.SubCategoryId}, "
                    + $"Code='{subCategory.Code}') has no matching row in mDomain.");
            }

            writer.AddRow(hierarchyId, subCategory.Code, subCategory.Name, domainId, string.Empty, null);
            bySubCategoryId[subCategory.SubCategoryId] = (hierarchyId, subCategory.Code, domainId);
            hierarchyId++;
        }

        return new HierarchyLoadResult((int)writer.RowsWritten, bySubCategoryId);
    }

    // ------------------------------------------------------------------
    // mMetric — EXACT mirror of the members of the MET domain that LoadMembers has just written.
    // One row per member, CorrespondingMemberID pointing to it — never the other way round:
    // mMetric has no code of its own, it inherits it from mMember.
    // ------------------------------------------------------------------

    private static int LoadMetrics(
        SqliteConnection destination,
        IReadOnlyList<(int MemberId, int PropertyId, string Code)> metMembers,
        IReadOnlyDictionary<int, string> dataTypeCodeByPropertyId,
        IReadOnlyDictionary<int, int> subCategoryIdByPropertyId,
        IReadOnlyDictionary<int, (int HierarchyId, string HierarchyCode, int DomainId)> hierarchyBySubCategoryId,
        List<string> unresolvedDataTypeCases)
    {
        using var writer = new SqliteBatchWriter(
            destination,
            "mMetric",
            [
                "MetricID", "CorrespondingMemberID", "DataType", "FlowType", "BalanceType",
                "ReferencedDomainID", "ReferencedHierarchyID", "HierarchyStartingMemberID",
                "IsStartingMemberIncluded", "IsAbstract", "CustomDataTypeID",
            ]);

        var metricId = 1;

        foreach (var member in metMembers)
        {
            var dataType = dataTypeCodeByPropertyId.TryGetValue(member.PropertyId, out var rawCode)
                ? ResolveDataType(rawCode, member.Code)
                : null;

            if (dataType is null)
            {
                // Only i, r, s, b, t, d, e, m, p, es are translated (s/es by the naming law). A
                // translation is not forced: the case is named and the row is skipped.
                unresolvedDataTypeCases.Add(
                    $"{member.Code} (PropertyID={member.PropertyId}): DataType.Code="
                    + $"'{rawCode ?? "<no Property>"}' has no known translation");
                continue;
            }

            int? referencedHierarchyId = null;
            int? referencedDomainId = null;
            if (subCategoryIdByPropertyId.TryGetValue(member.PropertyId, out var subCategoryId)
                && hierarchyBySubCategoryId.TryGetValue(subCategoryId, out var hierarchy))
            {
                referencedHierarchyId = hierarchy.HierarchyId;
                referencedDomainId = hierarchy.DomainId;
            }

            writer.AddRow(
                metricId,
                member.MemberId,
                dataType,
                "STOCK", // FlowType: constant (2,407 in the reference)
                null, // BalanceType: NULL — empty in the reference
                referencedDomainId,
                referencedHierarchyId,
                null, // HierarchyStartingMemberID: NULL — empty in the reference
                referencedHierarchyId is not null, // IsStartingMemberIncluded: 1 <=> has a referenced hierarchy
                null, // IsAbstract: NULL — empty in the reference
                null); // CustomDataTypeID: NULL — empty in the reference

            metricId++;
        }

        return (int)writer.RowsWritten;
    }

    // ------------------------------------------------------------------
    // mDimension — one row for each PropertyCategory row of a property that is a dimension — the
    // doubling (11 duplicates measured) COMES SOLELY from this, it is not programmed as a special
    // case.
    //
    // Measured REFINEMENT: in 5 of the 1,084 dimension properties the ItemCategory/_PR CODE is
    // ALSO versioned (StartReleaseID/EndReleaseID), not only the PropertyCategory domain — e.g. the
    // property of "LEA" is the SAME as that of "qLEA": the code changed from "LEA" to "qLEA"
    // exactly when the domain went from "CR" to "LE" (both windows [2,3) and [3,–) identical).
    // Emitting "the property code" as if it were a single one (the literal reading) loses the old
    // code and duplicates the new one under the wrong domain. The implemented rule — crossing the
    // window of EACH PropertyCategory row with the window of the ItemCategory/_PR in force IN THAT
    // SAME INTERVAL — reproduces the 5 measured cases against the reference exactly (LEA/qLEA,
    // LEB/qLEB, LES/qLES, ei912/old-ei912, FGT/old-FGT).
    // ------------------------------------------------------------------

    /// <summary>A validity window <c>[Start, End)</c>, with <c>End = null</c> meaning open-ended (in force until today).</summary>
    private static bool WindowsOverlap(int startA, int? endA, int startB, int? endB, out int overlapStart)
    {
        overlapStart = Math.Max(startA, startB);
        var overlapEndExclusive = endA is null ? endB : (endB is null ? endA : Math.Min(endA.Value, endB.Value));
        return overlapEndExclusive is null || overlapStart < overlapEndExclusive.Value;
    }

    private static int LoadDimensions(
        SqliteConnection destination,
        IReadOnlyCollection<int> dimensionPropertyIds,
        IReadOnlyDictionary<int, IReadOnlyList<(string Code, int StartReleaseId, int? EndReleaseId)>> propertyCodeWindowsById,
        IReadOnlyDictionary<int, string?> itemNameById,
        IReadOnlyDictionary<int, IReadOnlyList<Dpm20PropertyCategoryRow>> propertyCategoriesByPropertyId,
        IReadOnlyDictionary<int, int> domainIdByCategoryId,
        IReadOnlyDictionary<int, bool> isTypedDomainByDomainId,
        int naCategoryId,
        int intNaDomainId,
        int strNaDomainId,
        IReadOnlyDictionary<int, string> dataTypeCodeByPropertyId,
        IReadOnlyDictionary<int, string> releaseCodeByReleaseId,
        List<string> propertiesWithoutCode,
        List<string> propertiesWithoutPropertyCategory,
        List<string> unresolvedDomainCases)
    {
        using var writer = new SqliteBatchWriter(
            destination,
            "mDimension",
            ["DimensionID", "DimensionLabel", "DimensionCode", "DimensionDescription", "DimensionXBRLCode", "DomainID", "IsTypedDimension", "ConceptID"]);

        var dimensionId = 1;

        foreach (var propertyId in dimensionPropertyIds.OrderBy(id => id))
        {
            if (!propertyCodeWindowsById.TryGetValue(propertyId, out var codeWindows) || codeWindows.Count == 0)
            {
                // Measured: 0 of 1,084 (every dimension property has a code in ItemCategory/_PR).
                // It is not forced: it is named and skipped.
                propertiesWithoutCode.Add($"PropertyID={propertyId}");
                continue;
            }

            if (!propertyCategoriesByPropertyId.TryGetValue(propertyId, out var domainWindows) || domainWindows.Count == 0)
            {
                propertiesWithoutPropertyCategory.Add(codeWindows[0].Code);
                continue;
            }

            var label = itemNameById.TryGetValue(propertyId, out var name) ? name : null;

            foreach (var domainWindow in domainWindows)
            {
                // The code in force IN THE SAME WINDOW as this PropertyCategory row (see the
                // comment of the block): normally there is a single codeWindow and it always overlaps.
                foreach (var codeWindow in codeWindows)
                {
                    if (!WindowsOverlap(
                        codeWindow.StartReleaseId, codeWindow.EndReleaseId,
                        domainWindow.StartReleaseId, domainWindow.EndReleaseId,
                        out var overlapStartReleaseId))
                    {
                        continue;
                    }

                    var code = codeWindow.Code;
                    int domainId;

                    if (domainWindow.CategoryId == naCategoryId)
                    {
                        // TYPED dimension whose PropertyCategory is _NA → INT_NA/STR_NA according
                        // to the property's OWN DataType (untranslated). Measured 7 of 7.
                        if (!dataTypeCodeByPropertyId.TryGetValue(propertyId, out var rawType))
                        {
                            unresolvedDomainCases.Add($"{code} (PropertyID={propertyId}): _NA without its own DataType in Property");
                            continue;
                        }

                        domainId = rawType switch
                        {
                            "i" => intNaDomainId,
                            "s" => strNaDomainId,
                            _ => -1,
                        };

                        if (domainId == -1)
                        {
                            unresolvedDomainCases.Add(
                                $"{code} (PropertyID={propertyId}): _NA with DataType.Code='{rawType}', "
                                + "only 'i' (INT_NA) and 's' (STR_NA) are covered");
                            continue;
                        }
                    }
                    else if (!domainIdByCategoryId.TryGetValue(domainWindow.CategoryId, out domainId))
                    {
                        unresolvedDomainCases.Add(
                            $"{code} (PropertyID={propertyId}): PropertyCategory.CategoryID={domainWindow.CategoryId} "
                            + "has no matching row in mDomain");
                        continue;
                    }

                    if (!releaseCodeByReleaseId.TryGetValue(overlapStartReleaseId, out var releaseCode))
                    {
                        throw new InvalidOperationException(
                            $"PropertyCategory of '{code}' (PropertyID={propertyId}) crosses at ReleaseID="
                            + $"{overlapStartReleaseId}, which does not exist in [Release].");
                    }

                    var isTyped = isTypedDomainByDomainId.TryGetValue(domainId, out var typed) && typed;

                    writer.AddRow(
                        dimensionId,
                        label,
                        code,
                        string.Empty, // DimensionDescription: constant ''
                        $"eba_dim_{releaseCode}:{code}",
                        domainId,
                        isTyped,
                        null); // ConceptID: NULL until the concept loader runs

                    dimensionId++;
                }
            }
        }

        return (int)writer.RowsWritten;
    }

    // ------------------------------------------------------------------
    // Orchestration
    // ------------------------------------------------------------------

    /// <summary>
    /// Diagnostics of the derivations that are measured with named failures (4 failures and 11
    /// with no link in the referenced hierarchy of the metric, over 739; 7 failures and 6 without
    /// <c>PropertyCategory</c> in the dimension's domain, over 1,101). These figures are not
    /// forced: they are collected here for the report, they are not used to "correct" any row.
    /// </summary>
    public sealed record Diagnostics(
        IReadOnlyList<string> UnresolvedMetricDataTypeCases,
        IReadOnlyList<string> DimensionsWithoutCode,
        IReadOnlyList<string> DimensionsWithoutPropertyCategory,
        IReadOnlyList<string> UnresolvedDimensionDomainCases);

    /// <param name="reader">
    /// The DPM 2.0 reader, already open. All the reads specific to the dictionary are requested
    /// from it here (<c>Category</c>, <c>ItemCategory</c>, <c>Item</c>,
    /// <c>SuperCategoryComposition</c>, <c>SubCategory</c>, the two metric-identification reads,
    /// <c>DataType</c>, <c>Property</c>, <c>PropertyCategory</c>, <c>ContextComposition</c>, the
    /// <c>key</c> variables and <c>HeaderVersion</c>⋈<c>SubCategoryVersion</c>): unlike
    /// <see cref="Dpm20SkeletonLoader.Load"/>, which receives lists already read by the caller,
    /// the reader is passed here because there are more reads and all of them belong to this phase.
    /// </param>
    /// <param name="destination">Destination SQLite connection, already open on the created schema.</param>
    /// <param name="diagnostics">
    /// Output: the named cases that are not fully resolved (failures and "no link"/"no
    /// PropertyCategory"). They are reported, not forced.
    /// </param>
    public static Result Load(Dpm20AccessReader reader, SqliteConnection destination, out Diagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(destination);

        var categories = reader.ReadCategories().ToList();
        var domainResult = LoadDomains(destination, categories);

        var compositions = reader.ReadSuperCategoryCompositions().ToList();
        var domainUnionRows = LoadDomainUnions(destination, compositions, domainResult.DomainIdByCategoryId);

        var itemCategories = reader.ReadItemCategories().ToList();
        var itemNameById = reader.ReadItems().ToDictionary(i => i.ItemId, i => i.Name);
        var factPropertyIds = reader.ReadFactVariablePropertyIds().ToHashSet();
        var nonKeyHeaderPropertyIds = reader.ReadNonKeyHeaderPropertyIds().ToHashSet();

        var memberResult = LoadMembers(
            destination,
            itemCategories,
            itemNameById,
            domainResult.DomainIdByCategoryId,
            domainResult.DomainCodeByCategoryId,
            domainResult.MetDomainId,
            domainResult.SentinelDomainId,
            domainResult.PrCategoryId,
            domainResult.TeCategoryId,
            factPropertyIds,
            nonKeyHeaderPropertyIds);

        var subCategories = reader.ReadSubCategories().ToList();
        var hierarchyResult = LoadHierarchies(
            destination, subCategories, domainResult.DomainIdByCategoryId, domainResult.MetDomainId, domainResult.PrCategoryId);

        // ---- What mMetric and mDimension need in addition, on the SAME connection ----

        var dataTypeCodeById = reader.ReadDataTypes().ToDictionary(dt => dt.DataTypeId, dt => dt.Code);
        var dataTypeCodeByPropertyId = new Dictionary<int, string>();
        foreach (var property in reader.ReadProperties())
        {
            if (property.DataTypeId is { } dataTypeId && dataTypeCodeById.TryGetValue(dataTypeId, out var code))
            {
                dataTypeCodeByPropertyId[property.PropertyId] = code;
            }
        }

        // mMetric.ReferencedHierarchyID: PropertyID -> SubCategoryID, first row per HeaderVID
        // (36 of 537 properties have more than one distinct candidate — documented in
        // Dpm20HeaderVersionSubCategoryRow, not resolved by voting or averaging).
        var subCategoryIdByPropertyId = new Dictionary<int, int>();
        foreach (var row in reader.ReadHeaderVersionSubCategories())
        {
            subCategoryIdByPropertyId.TryAdd(row.PropertyId, row.SubCategoryId);
        }

        var unresolvedMetricDataTypeCases = new List<string>();
        var metricRows = LoadMetrics(
            destination,
            memberResult.MetMembers,
            dataTypeCodeByPropertyId,
            subCategoryIdByPropertyId,
            hierarchyResult.BySubCategoryId,
            unresolvedMetricDataTypeCases);

        // mDimension: properties = ContextComposition ∪ key variables.
        var dimensionPropertyIds = new HashSet<int>(reader.ReadContextCompositionPropertyIds());
        dimensionPropertyIds.UnionWith(reader.ReadKeyVariablePropertyIds());

        // The property code ("DimensionCode = the property code") comes from ItemCategory/_PR, the
        // SAME source as mMetric.CorrespondingMemberID.MemberCode. The WINDOW
        // [StartReleaseID, EndReleaseID) of each row is kept (not only the last one): in 5 of the
        // 1,084 dimension properties there is more than one code, and LoadDimensions decides which
        // one corresponds to each PropertyCategory row by crossing windows (see the block comment
        // of LoadDimensions above — a measured refinement).
        var propertyCodeWindowsById = new Dictionary<int, List<(string Code, int StartReleaseId, int? EndReleaseId)>>();
        foreach (var item in itemCategories)
        {
            if (item.CategoryId != domainResult.PrCategoryId)
            {
                continue;
            }

            if (!propertyCodeWindowsById.TryGetValue(item.ItemId, out var windows))
            {
                windows = [];
                propertyCodeWindowsById[item.ItemId] = windows;
            }

            windows.Add((item.Code, item.StartReleaseId, item.EndReleaseId));
        }

        var propertyCodeWindowsByIdReadOnly = propertyCodeWindowsById
            .ToDictionary(kv => kv.Key, IReadOnlyList<(string Code, int StartReleaseId, int? EndReleaseId)> (kv) => kv.Value);

        var propertyCategoriesByPropertyId = new Dictionary<int, List<Dpm20PropertyCategoryRow>>();
        foreach (var row in reader.ReadPropertyCategories())
        {
            if (!propertyCategoriesByPropertyId.TryGetValue(row.PropertyId, out var list))
            {
                list = [];
                propertyCategoriesByPropertyId[row.PropertyId] = list;
            }

            list.Add(row);
        }

        var propertyCategoriesByPropertyIdReadOnly = propertyCategoriesByPropertyId
            .ToDictionary(kv => kv.Key, IReadOnlyList<Dpm20PropertyCategoryRow> (kv) => kv.Value);

        var releaseCodeByReleaseId = reader.ReadReleases().ToDictionary(r => r.ReleaseId, r => r.Code);

        var dimensionsWithoutCode = new List<string>();
        var dimensionsWithoutPropertyCategory = new List<string>();
        var unresolvedDimensionDomainCases = new List<string>();
        var dimensionRows = LoadDimensions(
            destination,
            dimensionPropertyIds,
            propertyCodeWindowsByIdReadOnly,
            itemNameById,
            propertyCategoriesByPropertyIdReadOnly,
            domainResult.DomainIdByCategoryId,
            domainResult.IsTypedDomainByDomainId,
            domainResult.NaCategoryId,
            domainResult.IntNaDomainId,
            domainResult.StrNaDomainId,
            dataTypeCodeByPropertyId,
            releaseCodeByReleaseId,
            dimensionsWithoutCode,
            dimensionsWithoutPropertyCategory,
            unresolvedDimensionDomainCases);

        // The MET dimension has no real source to rename (unlike DPM 1.0) — it is synthesized.
        // It goes HERE, with the rest of mDimension (it is dictionary, not categorisation; every
        // DimensionID referenced by mOrdinateCategorisation must have its row in mDimension).
        // DimensionID = 9999, MEASURED identical in the 4.2 and 4.0 reference databases — not a
        // minted counter.
        WriteMetDimension(destination, domainResult.MetDomainId);
        dimensionRows++;

        diagnostics = new Diagnostics(
            unresolvedMetricDataTypeCases,
            dimensionsWithoutCode,
            dimensionsWithoutPropertyCategory,
            unresolvedDimensionDomainCases);

        return new Result(
            domainResult.RowsWritten,
            domainUnionRows,
            memberResult.RowsWritten,
            hierarchyResult.RowsWritten,
            metricRows,
            dimensionRows,
            hierarchyResult.BySubCategoryId);
    }

    /// <summary>
    /// The <c>mDimension</c> row for the <c>MET</c> metric dimension: <c>DimensionID = 9999</c>
    /// measured as a literal in two independent references, <c>DimensionXBRLCode = "MET"</c>
    /// WITHOUT the <c>eba_dim_{release}:</c> prefix carried by the rest of the dimensions (it is
    /// the exception, measured).
    /// </summary>
    private static void WriteMetDimension(SqliteConnection destination, int metDomainId)
    {
        using var writer = new SqliteBatchWriter(
            destination,
            "mDimension",
            ["DimensionID", "DimensionLabel", "DimensionCode", "DimensionDescription", "DimensionXBRLCode", "DomainID", "IsTypedDimension", "ConceptID"]);

        writer.AddRow(9999, "Metric dimension", "MET", string.Empty, "MET", metDomainId, false, null);
    }
}
