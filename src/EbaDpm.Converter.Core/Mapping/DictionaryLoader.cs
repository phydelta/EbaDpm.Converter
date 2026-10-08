using System.Globalization;
using System.Text.RegularExpressions;
using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Mapping;

/// <summary>
/// Loads the dictionary: <c>mDomain</c>, <c>mMember</c>, <c>mDimension</c>, <c>mMetric</c>,
/// <c>mHierarchy</c>, <c>mHierarchyNode</c> and <c>mConceptTranslation</c> (synthesised). It runs
/// after <see cref="SkeletonLoader.Load"/>, on the same connection: it reuses the <c>mRelease</c>
/// already written by the skeleton (it reads it back, it does not recompute it) and extends
/// <c>mConcept</c> with the concepts of the entities loaded here.
///
/// There is no taxonomy filter: the dictionary is shared between taxonomies, and pruning it
/// requires the table and axis layer. The figures are small (125 domains, 11,028 members, 864
/// dimensions, 1,886 metrics, 977 hierarchies, 18,390 nodes).
///
/// Out of scope here: <c>mTemplateOrTable</c>, <c>mTable</c>, <c>mTaxonomyTable</c>,
/// <c>mAxis</c>, <c>mTableAxis</c>, <c>mAxisOrdinate</c>, <c>mOrdinateCategorisation</c>,
/// <c>mOpenAxisValueRestriction</c>, <c>mTableCell</c>, <c>mCellPosition</c>, modules,
/// signatures.
/// </summary>
public static class DictionaryLoader
{
    /// <summary>Row count written per table, for the CLI report.</summary>
    /// <param name="MemberIdRenumbering">
    /// The <c>MemberID</c> translation applied to the occupant of 9999 (if there was one). It is
    /// exposed so that the later loaders (<c>mOrdinateCategorisation</c>,
    /// <c>mOpenAxisValueRestriction</c>) reuse the same single point instead of recomputing it.
    /// </param>
    public sealed record Result(
        int DomainRows,
        int MemberRows,
        int DimensionRows,
        int MetricRows,
        int HierarchyRows,
        int HierarchyNodeRows,
        int NewConceptRows,
        int ConceptTranslationRows,
        MemberIdRenumbering MemberIdRenumbering);

    // ------------------------------------------------------------------
    // XBRL code verbatim; the ReleaseID of Dimension/Member is READ from the suffix of their
    // own code, not composed
    // ------------------------------------------------------------------

    /// <summary>
    /// Release suffix at the end of the prefix of an XBRL code (e.g. <c>eba_dim_4.0</c>): the
    /// captured group is the <c>ReleaseCode</c>.
    /// </summary>
    private static readonly Regex ReleaseSuffixPattern = new(@"_(\d+(?:\.\d+)*)$", RegexOptions.Compiled);

    /// <summary>
    /// Extracts the <c>ReleaseCode</c> from the suffix of the prefix of an XBRL code:
    /// <c>eba_dim_4.0:EXC</c> → <c>4.0</c>. Without a suffix, or without the <c>':'</c> separator
    /// between prefix and remainder, it returns <see langword="null"/> (the concept has no release).
    /// </summary>
    internal static string? ExtractReleaseCodeFromXbrlSuffix(string? xbrlCode)
    {
        if (string.IsNullOrEmpty(xbrlCode))
        {
            return null;
        }

        var colonIndex = xbrlCode.IndexOf(':');
        if (colonIndex < 0)
        {
            return null; // unexpected format (not "prefix:rest"); no release can be derived.
        }

        var prefix = xbrlCode[..colonIndex];
        var match = ReleaseSuffixPattern.Match(prefix);

        return match.Success ? match.Groups[1].Value : null;
    }

    // ------------------------------------------------------------------
    // mDomain: DataTypeID denormalised to text; the metrics domain is renamed
    // ------------------------------------------------------------------

    /// <summary>
    /// Identifies the <c>DomainID</c> of the metrics domain by its role, not by the Access
    /// literal 100: it is the only domain whose set of <c>MemberID</c> values matches exactly the
    /// set of <c>MetricID</c> values of <c>Metric</c> (a structural relation, Access's own FK:
    /// <c>Metric.MetricID</c> is a <c>Member.MemberID</c>). Verified on Access v4.1: <c>DomainID</c>
    /// 100 (<c>DomainCode</c> "AT") has exactly the same 1,886 members as the 1,886 rows of
    /// <c>Metric</c>, and no other domain satisfies the equality. This criterion does not depend on
    /// literals that can change between Access releases (neither <c>DomainID</c>, nor
    /// <c>DomainCode</c>, nor <c>DomainXbrlCode</c>).
    /// </summary>
    private static int IdentifyMetricsDomainId(IDpmSourceReader source)
    {
        var metricMemberIds = new HashSet<int>(source.ReadMetrics().Select(m => m.MetricId));

        var memberIdsByDomain = new Dictionary<int, HashSet<int>>();
        foreach (var member in source.ReadMembers())
        {
            if (member.DomainId is not { } domainId)
            {
                continue;
            }

            if (!memberIdsByDomain.TryGetValue(domainId, out var members))
            {
                members = [];
                memberIdsByDomain[domainId] = members;
            }

            members.Add(member.MemberId);
        }

        var candidates = memberIdsByDomain
            .Where(kv => kv.Value.SetEquals(metricMemberIds))
            .Select(kv => kv.Key)
            .ToList();

        if (candidates.Count != 1)
        {
            throw new InvalidOperationException(
                $"The metrics domain could not be identified unambiguously. "
                + $"{candidates.Count} candidate domains whose member set matches "
                + $"Metric exactly: [{string.Join(",", candidates)}]. The role-based "
                + "identification criterion is no longer valid for this Access database.");
        }

        return candidates[0];
    }

    /// <summary>
    /// <c>DomainID</c> -&gt; <c>IsTypedDomain</c> of ALL the domains in Access, read once so that
    /// <see cref="LoadDimensions"/> derives <c>mDimension.IsTypedDimension</c> with nothing more
    /// than a lookup. A cheap sweep (125 rows), like <see cref="IdentifyMetricsDomainId"/> with
    /// Member.
    /// </summary>
    private static Dictionary<int, bool> BuildIsTypedDomainByDomainId(IDpmSourceReader source) =>
        source.ReadDomains().ToDictionary(d => d.DomainId, d => d.IsTypedDomain);

    /// <summary>
    /// <c>mDomain.DomainXBRLCode</c> is NOT verbatim from Access: it is COMPOSED. Access has
    /// <c>DomainXbrlCode</c> without a vocabulary prefix (e.g. <c>eba_AP</c>); the three target
    /// references have <c>eba_exp:</c> + <c>DomainCode</c> for an untyped domain, or
    /// <c>eba_typ:</c> + <c>DomainCode</c> for a typed one (e.g. <c>eba_exp:AP</c>). This is the
    /// fourth time the project has confused the source vocabulary with the target one (after the
    /// open-axis sentinel, the metrics dimension and the concept types): this function exists so
    /// that the composition cannot be overlooked in the code. It is NOT called for the metrics
    /// domain (fixed "MET") nor for the 9999 sentinel (empty string): those two cases are resolved
    /// BEFORE reaching here, in the caller.
    /// </summary>
    private static string ComposeDomainXbrlCode(string domainCode, bool isTypedDomain) =>
        (isTypedDomain ? "eba_typ:" : "eba_exp:") + domainCode;

    private static int LoadDomains(
        IDpmSourceReader source,
        SqliteConnection destination,
        IReadOnlyDictionary<int, string> dataTypeLabelById,
        int metricsDomainId,
        HashSet<int> reachableConceptIds,
        List<(int ConceptId, string? Label, string? Description)> translationSeeds)
    {
        using var writer = new SqliteBatchWriter(
            destination,
            "mDomain",
            ["DomainID", "DomainCode", "DomainLabel", "DomainDescription", "DomainXBRLCode", "DataType", "IsTypedDomain", "IsNillable", "ConceptID"]);

        // ID 9999 is reserved for the sentinel domain added after the loop. Measured free in
        // Access v4.1 (0 rows); if a future Access occupied it, stop here instead of silently
        // overwriting it. Unlike MemberID, there is no renumbering mechanism for DomainID.
        var reservedDomainIdOccupied = false;

        foreach (var domain in source.ReadDomains())
        {
            if (domain.DomainId == PreferredSyntheticId)
            {
                reservedDomainIdOccupied = true;
            }

            var dataType = domain.DataTypeId is { } dataTypeId && dataTypeLabelById.TryGetValue(dataTypeId, out var label)
                ? label
                : null;

            // The metrics domain is emitted with the business key of the three references
            // (MET / "Metric domain" / MET), not with Access's (AT / "Metric" / eba_met). Only
            // these three fields change; DomainDescription and the rest of the row keep coming
            // from Access verbatim.
            var isMetricsDomain = domain.DomainId == metricsDomainId;
            var domainCode = isMetricsDomain ? "MET" : domain.DomainCode;
            var domainLabel = isMetricsDomain ? "Metric domain" : domain.DomainLabel;
            var domainXbrlCode = isMetricsDomain ? "MET" : ComposeDomainXbrlCode(domain.DomainCode, domain.IsTypedDomain);

            writer.AddRow(
                domain.DomainId,
                domainCode,
                domainLabel,
                domain.DomainDescription,
                domainXbrlCode,
                dataType,
                domain.IsTypedDomain,
                null, // IsNillable: no source in Access
                domain.ConceptId);

            if (domain.ConceptId is { } conceptId)
            {
                reachableConceptIds.Add(conceptId);

                // The synthesised translation follows the same label as the emitted row: the 4.2
                // reference has "Metric domain" as the translation Text of this ConceptID, not
                // "Metric".
                translationSeeds.Add((conceptId, domainLabel, domain.DomainDescription));
            }
        }

        if (reservedDomainIdOccupied)
        {
            throw new InvalidOperationException(
                $"The DomainID {PreferredSyntheticId} reserved for the \"Open\" sentinel domain "
                + "is occupied by a real Access domain. There is no renumbering for DomainID "
                + "(unlike MemberID); the decision needs to be revisited.");
        }

        // Sentinel domain "Open": MemberCode/DomainCode/DomainXBRLCode as empty strings, not
        // NULL (that is how 4.0, 4.2 and EIOPA do it). No ConceptID (all four references agree on
        // NULL), so it does not enter reachableConceptIds nor translationSeeds.
        writer.AddRow(
            PreferredSyntheticId,
            string.Empty,
            "Open",
            null, // DomainDescription: no equivalent in the four references
            string.Empty,
            null, // DataType: NULL
            false, // IsTypedDomain
            null, // IsNillable: no source
            null); // ConceptID: NULL

        return (int)writer.RowsWritten;
    }

    // ------------------------------------------------------------------
    // mMember: direct, MemberXBRLCode literal (no suffix); the release of the concept is read
    // (not composed) from the suffix of that same code
    // ------------------------------------------------------------------

    /// <summary>
    /// The "open value" sentinel IN ACCESS: <c>Member.MemberID</c> = 999 (<c>MemberCode</c>
    /// <c>x999</c>, <c>MemberLabel</c> <c>&lt;Key value&gt;</c>, no domain and no XBRL code). It
    /// is not the same object as the TARGET sentinel (9999): this one is CONSUMED when loading
    /// <c>mMember</c> (no row is emitted for it) because the axis loader translates it to the 9999
    /// sentinel when reading <c>OrdinateCategorisation</c>.
    /// </summary>
    private const int AccessOpenValueSentinelMemberId = 999;

    private static int LoadMembers(
        IDpmSourceReader source,
        SqliteConnection destination,
        HashSet<int> reachableConceptIds,
        List<(int ConceptId, string? Label, string? Description)> translationSeeds,
        Dictionary<int, string> releaseCodeByConceptId,
        MemberIdRenumbering memberIdRenumbering,
        int sentinelMemberConceptId,
        Dictionary<int, string?> memberLabelByDestinationMemberId)
    {
        using var writer = new SqliteBatchWriter(
            destination,
            "mMember",
            ["MemberID", "DomainID", "MemberCode", "MemberLabel", "MemberXBRLCode", "IsDefaultMember", "ConceptID"]);

        foreach (var member in source.ReadMembers())
        {
            if (member.MemberId == AccessOpenValueSentinelMemberId)
            {
                continue; // consumed, not emitted (replaced by the 9999 sentinel below)
            }

            var destinationMemberId = memberIdRenumbering.Translate(member.MemberId);

            writer.AddRow(
                destinationMemberId,
                member.DomainId,
                member.MemberCode,
                member.MemberLabel,
                member.MemberXbrlCode, // literal, no suffix, also for the members that are metrics
                member.IsDefaultMember,
                member.ConceptId);

            // MemberLabel by TARGET MemberID: reused by LoadHierarchyNodes to seed the translation
            // of the HierarchyNode that represents this member in a hierarchy (that entity has no
            // HierarchyNodeLabel of its own in Access).
            memberLabelByDestinationMemberId[destinationMemberId] = member.MemberLabel;

            if (member.ConceptId is { } conceptId)
            {
                reachableConceptIds.Add(conceptId);
                translationSeeds.Add((conceptId, member.MemberLabel, member.MemberDescription));

                if (ExtractReleaseCodeFromXbrlSuffix(member.MemberXbrlCode) is { } releaseCode)
                {
                    releaseCodeByConceptId[conceptId] = releaseCode;
                }
            }
        }

        // Sentinel member "Open": MemberCode as an empty string (not NULL), MemberXBRLCode NULL
        // (all four references agree), synthetic ConceptID.
        writer.AddRow(
            PreferredSyntheticId,
            PreferredSyntheticId, // DomainID: the sentinel domain, also 9999
            string.Empty,
            "Open",
            null,
            false, // IsDefaultMember
            sentinelMemberConceptId);

        memberLabelByDestinationMemberId[PreferredSyntheticId] = "Open";
        reachableConceptIds.Add(sentinelMemberConceptId);
        translationSeeds.Add((sentinelMemberConceptId, "Open", null));

        return (int)writer.RowsWritten;
    }

    // ------------------------------------------------------------------
    // mDimension: DimensionXBRLCode verbatim, no suffix composed; the release of the concept is
    // read from the suffix of that same code; the metrics dimension is RENAMED, not synthesised
    // (Access does model it, as "ATY", DimensionID 100, with 31,682 categorisations)
    // ------------------------------------------------------------------

    /// <summary>
    /// Preferred synthetic ID for the objects with no source in Access that are still synthesised
    /// (the "Open" sentinel): 9999 if free, otherwise <c>MAX+1</c> of the corresponding table. It is
    /// a purely synthetic ID: no comparison depends on its value (everything goes by business
    /// key), so it only needs not to collide with any real Access ID.
    /// </summary>
    private const int PreferredSyntheticId = 9999;

    /// <summary>
    /// Identifies the <c>DimensionID</c> of the metrics dimension by its role, never by the
    /// literal <c>ATY</c> nor by Access ID 100: it is the dimension whose <c>DomainID</c> is the
    /// metrics domain identified by <see cref="IdentifyMetricsDomainId"/>. Independent
    /// corroboration, not an identification criterion: that same dimension must be, and be the
    /// only one, of the 864 Access dimensions with a NULL <c>DimensionXbrlCode</c>. If the source
    /// has changed and that assertion does not hold, stop here instead of silently identifying the
    /// wrong dimension.
    /// </summary>
    private static int IdentifyMetricsDimensionId(IDpmSourceReader source, int metricsDomainId)
    {
        var domainCandidates = new List<int>();
        var nullXbrlCodeCandidates = new List<int>();

        foreach (var dimension in source.ReadDimensions())
        {
            if (dimension.DomainId == metricsDomainId)
            {
                domainCandidates.Add(dimension.DimensionId);
            }

            if (dimension.DimensionXbrlCode is null)
            {
                nullXbrlCodeCandidates.Add(dimension.DimensionId);
            }
        }

        if (domainCandidates.Count != 1)
        {
            throw new InvalidOperationException(
                "The metrics dimension could not be identified unambiguously by its role "
                + $"(DomainID={metricsDomainId}, the metrics domain). "
                + $"{domainCandidates.Count} candidate dimensions: [{string.Join(",", domainCandidates)}]. "
                + "The role-based identification criterion is no longer valid for this Access database.");
        }

        if (nullXbrlCodeCandidates.Count != 1 || nullXbrlCodeCandidates[0] != domainCandidates[0])
        {
            throw new InvalidOperationException(
                "The consistency assertion failed. The metrics dimension identified by role "
                + $"(DimensionID={domainCandidates[0]}) was expected to be the "
                + "ONLY one of the 864 Access dimensions with a NULL DimensionXBRLCode, but there are "
                + $"{nullXbrlCodeCandidates.Count} candidates: [{string.Join(",", nullXbrlCodeCandidates)}]. "
                + "The source has changed relative to what was measured; revisit the decision before continuing.");
        }

        return domainCandidates[0];
    }

    /// <summary>
    /// Synthetic <c>ConceptID</c> of an entity with no source in Access (the "Open" sentinel):
    /// 9999 if it does not already exist in Access's <c>Concept</c> NOR has already been allocated
    /// by a previous call in this same load, otherwise <c>MAX(ConceptID)+1</c> (incrementing while
    /// it collides with what has already been allocated). The occupancy check in Access is a
    /// point query (<see cref="IDpmSourceReader.ReadConceptsByIds"/>); the MAX fallback is
    /// aggregated in the Access engine (<see cref="IDpmSourceReader.ReadMaxConceptId"/>): the
    /// 123,025 rows of <c>Concept</c> are never transferred. <paramref name="reservedConceptIds"/>
    /// is mutated with the returned ID, so that a later call in the same load does not repeat it.
    /// </summary>
    private static int DetermineSyntheticConceptId(IDpmSourceReader source, ISet<int> reservedConceptIds)
    {
        if (!reservedConceptIds.Contains(PreferredSyntheticId) && !source.ReadConceptsByIds([PreferredSyntheticId]).Any())
        {
            reservedConceptIds.Add(PreferredSyntheticId);
            return PreferredSyntheticId;
        }

        var candidate = source.ReadMaxConceptId() + 1;
        while (reservedConceptIds.Contains(candidate))
        {
            candidate++;
        }

        reservedConceptIds.Add(candidate);
        return candidate;
    }

    /// <summary>
    /// Translation of the <c>MemberID</c> that occupies 9999 in Access. ID 9999 is reserved for the
    /// "Open" sentinel member; if Access already uses it (v4.1: <c>C27_12</c>, NACE, domain 280),
    /// it is renumbered to <c>MAX(MemberID)+1</c>. 11,028 rows: a cheap sweep, like
    /// <see cref="IdentifyMetricsDimensionId"/> with Dimension.
    /// </summary>
    private static MemberIdRenumbering DetermineMemberIdRenumbering(IDpmSourceReader source)
    {
        var memberIds = source.ReadMembers().Select(m => m.MemberId).ToList();

        if (!memberIds.Contains(PreferredSyntheticId))
        {
            return MemberIdRenumbering.Identity;
        }

        var renumberedOccupantId = memberIds.Max() + 1;
        return MemberIdRenumbering.ForOccupant(PreferredSyntheticId, renumberedOccupantId);
    }

    private static int LoadDimensions(
        IDpmSourceReader source,
        SqliteConnection destination,
        int metricsDimensionId,
        IReadOnlyDictionary<int, bool> isTypedDomainByDomainId,
        HashSet<int> reachableConceptIds,
        List<(int ConceptId, string? Label, string? Description)> translationSeeds,
        Dictionary<int, string> releaseCodeByConceptId)
    {
        var dimensionRows = 0;

        using var writer = new SqliteBatchWriter(
            destination,
            "mDimension",
            ["DimensionID", "DimensionLabel", "DimensionCode", "DimensionDescription", "DimensionXBRLCode", "DomainID", "IsTypedDimension", "ConceptID", "DefaultMemberID"]);

        foreach (var dimension in source.ReadDimensions())
        {
            // The metrics dimension ("ATY" in Access) is RENAMED when emitted, just as its domain
            // is renamed, keeping its source DimensionID and ConceptID. A separate row is NOT
            // synthesised.
            var isMetricsDimension = dimension.DimensionId == metricsDimensionId;
            var dimensionLabel = isMetricsDimension ? "Metric dimension" : dimension.DimensionLabel;
            var dimensionCode = isMetricsDimension ? "MET" : dimension.DimensionCode;
            var dimensionXbrlCode = isMetricsDimension ? "MET" : dimension.DimensionXbrlCode;

            // IsTypedDimension is DERIVED: it is the IsTypedDomain of the dimension's domain,
            // without exception (not even the metrics dimension: its MET domain is not typed).
            // Measured in the three references: it matches 365/365, 1,319/1,319, 1,101/1,101.
            if (!isTypedDomainByDomainId.TryGetValue(dimension.DomainId, out var isTypedDimension))
            {
                throw new InvalidOperationException(
                    $"DimensionID={dimension.DimensionId} references DomainID={dimension.DomainId}, "
                    + "which does not exist in mDomain. IsTypedDimension cannot be derived.");
            }

            writer.AddRow(
                dimension.DimensionId,
                dimensionLabel,
                dimensionCode,
                dimension.DimensionDescription,
                dimensionXbrlCode,
                dimension.DomainId,
                isTypedDimension, // derived from the IsTypedDomain of the domain, not copied
                dimension.ConceptId,
                null); // DefaultMemberID: no reliable source (open point)

            dimensionRows++;

            if (dimension.ConceptId is { } conceptId)
            {
                reachableConceptIds.Add(conceptId);
                translationSeeds.Add((conceptId, dimensionLabel, dimension.DimensionDescription));

                // The release is read from the source's OWN XBRL code suffix, not from the renamed
                // one: the metrics dimension has no suffix in either (its Access DimensionXbrlCode
                // is NULL), so it makes no practical difference, but the right source is still
                // dimension.DimensionXbrlCode.
                if (ExtractReleaseCodeFromXbrlSuffix(dimension.DimensionXbrlCode) is { } releaseCode)
                {
                    releaseCodeByConceptId[conceptId] = releaseCode;
                }
            }
        }

        return dimensionRows;
    }

    // ------------------------------------------------------------------
    // mMetric: DataTypeID/FlowTypeID denormalised; MetricID = Member.MemberID
    // ------------------------------------------------------------------

    private static int LoadMetrics(
        IDpmSourceReader source,
        SqliteConnection destination,
        IReadOnlyDictionary<int, string> dataTypeLabelById,
        IReadOnlyDictionary<int, string> flowTypeLabelById)
    {
        using var writer = new SqliteBatchWriter(
            destination,
            "mMetric",
            ["MetricID", "CorrespondingMemberID", "DataType", "FlowType", "BalanceType", "ReferencedDomainID", "ReferencedHierarchyID", "HierarchyStartingMemberID", "IsStartingMemberIncluded", "IsAbstract", "CustomDataTypeID"]);

        foreach (var metric in source.ReadMetrics())
        {
            var dataType = dataTypeLabelById.TryGetValue(metric.DataTypeId, out var dt) ? dt : null;
            var flowType = flowTypeLabelById.TryGetValue(metric.FlowTypeId, out var ft) ? ft : null;

            writer.AddRow(
                metric.MetricId,
                metric.MetricId, // CorrespondingMemberID = MetricID: the metric IS the member with the same ID
                dataType,
                flowType,
                null, // BalanceType: no reliable source; in the 3.2 reference all 912 mMetric
                      // rows also leave it NULL (Metric.Additivity is not Credit/Debit, and
                      // Access has no BalanceTypeID in Metric)
                metric.CodeDomainId, // ReferencedDomainID <- CodeDomainID
                metric.CodeSubdomainId, // ReferencedHierarchyID <- CodeSubdomainID
                null, // HierarchyStartingMemberID: no source
                null, // IsStartingMemberIncluded: likewise
                null, // IsAbstract: likewise
                null); // CustomDataTypeID: mCustomDataType is empty
        }

        return (int)writer.RowsWritten;
    }

    // ------------------------------------------------------------------
    // mHierarchy: direct
    // ------------------------------------------------------------------

    private static int LoadHierarchies(
        IDpmSourceReader source,
        SqliteConnection destination,
        HashSet<int> reachableConceptIds,
        List<(int ConceptId, string? Label, string? Description)> translationSeeds)
    {
        using var writer = new SqliteBatchWriter(
            destination,
            "mHierarchy",
            ["HierarchyID", "HierarchyCode", "HierarchyLabel", "DomainID", "HierarchyDescription", "ConceptID"]);

        foreach (var hierarchy in source.ReadHierarchies())
        {
            writer.AddRow(
                hierarchy.HierarchyId,
                hierarchy.HierarchyCode,
                hierarchy.HierarchyLabel,
                hierarchy.DomainId,
                hierarchy.HierarchyDescription,
                hierarchy.ConceptId);

            if (hierarchy.ConceptId is { } conceptId)
            {
                reachableConceptIds.Add(conceptId);
                translationSeeds.Add((conceptId, hierarchy.HierarchyLabel, hierarchy.HierarchyDescription));
            }
        }

        return (int)writer.RowsWritten;
    }

    // ------------------------------------------------------------------
    // mHierarchyNode: Path/Level/ParentMemberID are NORMALISED, not copied verbatim.
    // HierarchyNodeID is synthetic because Access has no single-column ID (and the PK is NOT NULL)
    // ------------------------------------------------------------------

    /// <summary>
    /// Groups by <c>HierarchyID</c> a sequence already ordered by (<c>HierarchyID</c>,
    /// <c>MemberID</c>), the one returned by <see cref="IDpmSourceReader.ReadHierarchyNodes"/>,
    /// without materialising more than the hierarchy in progress. The normalisation needs to see
    /// all the nodes of a hierarchy at once (to walk up the <c>ParentMemberID</c> chain), but it
    /// is not necessary to hold all 18,390 rows in memory simultaneously.
    /// </summary>
    private static IEnumerable<List<AccessHierarchyNodeRow>> GroupConsecutiveByHierarchy(
        IEnumerable<AccessHierarchyNodeRow> nodes)
    {
        List<AccessHierarchyNodeRow>? current = null;
        var currentHierarchyId = 0;

        foreach (var node in nodes)
        {
            if (current is null || node.HierarchyId != currentHierarchyId)
            {
                if (current is not null)
                {
                    yield return current;
                }

                current = [];
                currentHierarchyId = node.HierarchyId;
            }

            current.Add(node);
        }

        if (current is not null)
        {
            yield return current;
        }
    }

    /// <summary>
    /// Final <c>Path</c> of a node after normalisation: if the Access <c>Path</c> is well formed
    /// it is used as is; otherwise it is rebuilt by walking up the <c>ParentMemberID</c> chain.
    /// </summary>
    private static string NormalizeHierarchyNodePath(
        AccessHierarchyNodeRow node, IReadOnlyDictionary<int, AccessHierarchyNodeRow> nodesByMemberId)
        => IsWellFormedHierarchyNodePath(node.Path, node.MemberId)
            ? node.Path!
            : ReconstructHierarchyNodePath(node, nodesByMemberId);

    /// <summary>
    /// A <c>Path</c> is "well formed" if it ends in the node's own <c>MemberID</c>, with the dot
    /// as separator and terminator (<c>3677.1073.1042.</c>), never the comma, whatever the EIOPA
    /// documentation says: the data measured in Access and in the three references prevails.
    /// </summary>
    private static bool IsWellFormedHierarchyNodePath(string? path, int memberId)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments[^1] == memberId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Rebuilds the <c>Path</c> by walking up <c>ParentMemberID</c> from <paramref name="node"/>
    /// to the root of its hierarchy, and concatenating the <c>MemberID</c> values from the root
    /// down to the node, with a dot after each one. Guarded, in this order, against: a null
    /// <c>ParentMemberID</c> (end of the walk, root found); the self-reference of a root
    /// (<c>ParentMemberID</c> = its own <c>MemberID</c>; there are 4 in Access, they are roots,
    /// not loops); and an already visited cycle (end of the walk, defensive protection). A
    /// <c>ParentMemberID</c> pointing to a <c>MemberID</c> absent from this hierarchy is **not**
    /// absorbed as if it were a root: silently repairing it would graft an entire subtree in the
    /// wrong place without anyone noticing. It is named and the run stops.
    /// </summary>
    private static string ReconstructHierarchyNodePath(
        AccessHierarchyNodeRow node, IReadOnlyDictionary<int, AccessHierarchyNodeRow> nodesByMemberId)
    {
        var chain = new List<int> { node.MemberId };
        var visited = new HashSet<int> { node.MemberId };
        var current = node;

        while (true)
        {
            var parentId = current.ParentMemberId;

            if (parentId is null
                || parentId.Value == current.MemberId
                || !visited.Add(parentId.Value))
            {
                break;
            }

            if (!nodesByMemberId.TryGetValue(parentId.Value, out var parentNode))
            {
                throw new InvalidOperationException(
                    $"Cannot rebuild the Path of HierarchyID={node.HierarchyId}, "
                    + $"MemberID={node.MemberId}: the node MemberID={current.MemberId} has "
                    + $"ParentMemberID={parentId.Value}, which does not exist as a node of that hierarchy. "
                    + "It is not repaired silently: it would graft a subtree in the wrong place.");
            }

            chain.Add(parentId.Value);
            current = parentNode;
        }

        chain.Reverse();
        return string.Concat(chain.Select(id => id.ToString(CultureInfo.InvariantCulture) + "."));
    }

    /// <summary>
    /// Translates, row by row, the Access <c>MemberID</c> to the emitted ID, also inside the
    /// <c>Path</c> text, segment by segment, not only in <c>MemberID</c>/<c>ParentMemberID</c>:
    /// if the renumbered occupant is an ancestor of other nodes, its old ID appears embedded in
    /// the middle of the <c>Path</c> of those descendants, and the emitted <c>Path</c> must be
    /// consistent with the real <c>MemberID</c> of each node. It is applied BEFORE
    /// <see cref="GroupConsecutiveByHierarchy"/> and the path normalisation, so that both always
    /// work on already translated IDs.
    /// </summary>
    private static IEnumerable<AccessHierarchyNodeRow> ApplyMemberIdRenumbering(
        IEnumerable<AccessHierarchyNodeRow> nodes, MemberIdRenumbering memberIdRenumbering)
    {
        foreach (var node in nodes)
        {
            yield return node with
            {
                MemberId = memberIdRenumbering.Translate(node.MemberId),
                ParentMemberId = memberIdRenumbering.TranslateNullable(node.ParentMemberId),
                Path = TranslateHierarchyNodePathMemberIds(node.Path, memberIdRenumbering),
            };
        }
    }

    /// <summary>Translates each segment (a <c>MemberID</c> as text) of the raw Access <c>Path</c>.</summary>
    private static string? TranslateHierarchyNodePathMemberIds(string? path, MemberIdRenumbering memberIdRenumbering)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(segments.Select(
            segment => memberIdRenumbering.Translate(int.Parse(segment, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture) + "."));
    }

    private static int LoadHierarchyNodes(
        IDpmSourceReader source,
        SqliteConnection destination,
        HashSet<int> reachableConceptIds,
        List<(int ConceptId, string? Label, string? Description)> translationSeeds,
        MemberIdRenumbering memberIdRenumbering,
        IReadOnlyDictionary<int, string?> memberLabelByDestinationMemberId)
    {
        using var writer = new SqliteBatchWriter(
            destination,
            "mHierarchyNode",
            ["HierarchyNodeID", "HierarchyID", "MemberID", "IsAbstract", "ComparisonOperator", "UnaryOperator", "Order", "Level", "ParentMemberID", "HierarchyNodeLabel", "ConceptID", "Path"]);

        // Synthetic and deterministic: source.ReadHierarchyNodes() already comes ordered by
        // (HierarchyID, MemberID), so a counter advancing in streaming is enough. The order is
        // preserved after ApplyMemberIdRenumbering: it is a 1:1 translation, it does not reorder.
        var nextHierarchyNodeId = 1;

        foreach (var hierarchyNodes in GroupConsecutiveByHierarchy(ApplyMemberIdRenumbering(source.ReadHierarchyNodes(), memberIdRenumbering)))
        {
            var nodesByMemberId = hierarchyNodes.ToDictionary(n => n.MemberId);

            foreach (var node in hierarchyNodes)
            {
                var path = NormalizeHierarchyNodePath(node, nodesByMemberId);
                var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
                var level = segments.Length;
                var parentMemberId = segments.Length >= 2
                    ? int.Parse(segments[^2], CultureInfo.InvariantCulture)
                    : (int?)null;

                writer.AddRow(
                    nextHierarchyNodeId,
                    node.HierarchyId,
                    node.MemberId,
                    node.IsAbstract,
                    node.ComparisonOperator,
                    node.UnaryOperator,
                    node.Order,
                    level, // derived from the final Path, not copied from Access
                    parentMemberId, // likewise
                    null, // HierarchyNodeLabel: no source in Access; its translation is seeded below
                    node.ConceptId,
                    path); // verbatim if well formed, rebuilt otherwise

                nextHierarchyNodeId++;

                if (node.ConceptId is { } conceptId)
                {
                    reachableConceptIds.Add(conceptId);

                    // HierarchyNodeLabel does not exist in Access (column with no source, see
                    // above); the node translation is seeded with the MemberLabel of the member
                    // it represents, already emitted verbatim in mMember, not with an invented
                    // text. If the member has no MemberLabel (whitespace/null),
                    // ConceptTranslationWriter simply writes no row for this ConceptID: it is
                    // reported as a residue, not filled in by guesswork.
                    if (memberLabelByDestinationMemberId.TryGetValue(node.MemberId, out var memberLabel))
                    {
                        translationSeeds.Add((conceptId, memberLabel, null));
                    }
                }
            }
        }

        return (int)writer.RowsWritten;
    }

    // ------------------------------------------------------------------
    // mConceptTranslation: synthesised, shared writer in ConceptTranslationWriter
    // ------------------------------------------------------------------

    /// <summary>
    /// Dictionary seeds: Domain, Member, Dimension, Hierarchy (their own <c>*Label</c>) and
    /// HierarchyNode (no <c>HierarchyNodeLabel</c> of its own in Access, since the column does not
    /// exist; it is seeded with the <c>MemberLabel</c> of the member that represents that position
    /// in the hierarchy, already emitted verbatim in <c>mMember</c>; it is not invented data, it is
    /// the same text that identifies that member anywhere else in the output).
    ///
    /// The PK of mConceptTranslation is (ConceptID, LanguageID, Role): a ConceptID can only have
    /// one 'label' row and one 'description' row. Access does not guarantee this for every
    /// entity: 15 of the 977 Hierarchy rows with a ConceptID share that ConceptID with a different
    /// Hierarchy (a data defect in the source, not in this mapping; Domain/Member/Dimension were
    /// checked to have no cross or internal collisions), and HierarchyNode shares ConceptID in 192
    /// of 18,390 rows (not corrected: it is the same business object).
    /// <see cref="ConceptTranslationWriter.Write"/> keeps the first translation seen for each
    /// (ConceptID, Role); the generation order of the seeds (Domain, Member, Dimension, Hierarchy,
    /// HierarchyNode) makes that choice deterministic and reproducible.
    /// </summary>
    private static int LoadConceptTranslations(
        SqliteConnection destination,
        IEnumerable<(int ConceptId, string? Label, string? Description)> seeds)
        => ConceptTranslationWriter.Write(destination, seeds);

    // ------------------------------------------------------------------
    // mConcept: extended with the new dictionary concepts; ReleaseID only for Dimension and
    // Member, with the release named by the suffix of their own XBRL code. Domain, Hierarchy,
    // HierarchyNode and any other ConceptType get ReleaseID NULL.
    // The synthetic ConceptID of the "Open" sentinel member does not exist in Concept, so it is
    // written separately. The metrics dimension keeps its source ConceptID (not synthetic) and
    // flows through the normal path of reachableConceptIds/ReadConceptsByIds, like any Dimension.
    // ------------------------------------------------------------------

    private static int LoadNewConcepts(
        IDpmSourceReader source,
        SqliteConnection destination,
        HashSet<int> reachableConceptIds,
        IReadOnlyDictionary<int, string> releaseCodeByConceptId,
        IReadOnlyDictionary<string, int> releaseIdByCode,
        IReadOnlyList<(int ConceptId, string ConceptType)> syntheticConcepts,
        int accessOwnerId)
    {
        var alreadyLoaded = new HashSet<int>();
        using (var command = destination.CreateCommand())
        {
            command.CommandText = "SELECT \"ConceptID\" FROM mConcept";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                alreadyLoaded.Add(reader.GetInt32(0));
            }
        }

        var newIds = reachableConceptIds.Except(alreadyLoaded).ToList();

        using var writer = new SqliteBatchWriter(
            destination, "mConcept", ["ConceptID", "ConceptType", "OwnerID", "ReleaseID", "CreationDate", "ModificationDate", "FromDate", "ToDate"]);

        // Synthetic rows (the "Open" sentinel) do not come from Concept: they are removed from
        // newIds so that ReadConceptsByIds does not look for them in vain, and they are written
        // with the fixed values: the Access OwnerID, ReleaseID NULL, NULL dates.
        foreach (var (syntheticConceptId, syntheticConceptType) in syntheticConcepts)
        {
            if (newIds.Remove(syntheticConceptId))
            {
                writer.AddRow(syntheticConceptId, syntheticConceptType, accessOwnerId, null, null, null, null, null);
            }
        }

        foreach (var concept in source.ReadConceptsByIds(newIds))
        {
            int? releaseId = null;

            // releaseCodeByConceptId only has entries for the ConceptIDs of Dimension/Member with
            // a release suffix in their own XBRL code; everything else stays NULL.
            if (releaseCodeByConceptId.TryGetValue(concept.ConceptId, out var releaseCode))
            {
                if (!releaseIdByCode.TryGetValue(releaseCode, out var resolvedReleaseId))
                {
                    // Invariant: mRelease runs from the dynamic floor upwards and the floor is
                    // never later than 3.4, so any suffix observable in Access is covered. If it
                    // is not, it is a data error or an error in the floor computation, and it
                    // must stop here instead of silently writing a NULL.
                    throw new InvalidOperationException(
                        $"Could not resolve ReleaseID for ConceptID={concept.ConceptId} "
                        + $"(ConceptType={concept.ConceptType}, derived release='{releaseCode}'). "
                        + "mRelease does not contain that release.");
                }

                releaseId = resolvedReleaseId;
            }

            writer.AddRow(
                concept.ConceptId,
                concept.ConceptType,
                concept.OwnerId,
                releaseId,
                concept.CreationDate,
                concept.ModificationDate,
                concept.FromDate,
                concept.ToDate);
        }

        return (int)writer.RowsWritten;
    }

    // ------------------------------------------------------------------
    // Orchestration
    // ------------------------------------------------------------------

    /// <summary>
    /// Loads the seven dictionary pieces into <paramref name="destination"/>. It must run after
    /// <see cref="SkeletonLoader.Load"/> on the same connection.
    /// </summary>
    /// <param name="source">Access reader, already open.</param>
    /// <param name="destination">
    /// Target SQLite connection, already open and with the skeleton already loaded: in
    /// particular <c>mRelease</c> and the partial <c>mConcept</c> of the skeleton must exist.
    /// </param>
    /// <param name="allTaxonomies">All the taxonomies in Access, unfiltered (as in <see cref="SkeletonLoader"/>).</param>
    /// <param name="selectedTaxonomies">The taxonomies resulting from applying the taxonomy filter.
    /// It is not used directly here (the release is derived from the XBRL code suffix, not from
    /// CreationDate), but it is kept in the signature for symmetry with
    /// <see cref="SkeletonLoader.Load"/> and in case a future dictionary piece needs it.</param>
    public static Result Load(
        IDpmSourceReader source,
        SqliteConnection destination,
        IReadOnlyList<AccessTaxonomyRow> allTaxonomies,
        IReadOnlyList<AccessTaxonomyRow> selectedTaxonomies)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(allTaxonomies);
        ArgumentNullException.ThrowIfNull(selectedTaxonomies);

        var dataTypeLabelById = source.ReadDataTypes().ToDictionary(d => d.DataTypeId, d => d.DataTypeLabel);
        var flowTypeLabelById = source.ReadFlowTypes().ToDictionary(f => f.FlowTypeId, f => f.FlowTypeLabel);

        // The numeric ReleaseID is read back from mRelease (already written by the skeleton): it
        // is the single source of truth for that numbering.
        var releaseIdByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var command = destination.CreateCommand())
        {
            command.CommandText = "SELECT \"ReleaseID\", \"ReleaseCode\" FROM mRelease";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                releaseIdByCode[reader.GetString(1)] = reader.GetInt32(0);
            }
        }

        var reachableConceptIds = new HashSet<int>();
        var translationSeeds = new List<(int ConceptId, string? Label, string? Description)>();

        // ConceptID -> ReleaseCode, only for the Dimension/Member whose own XBRL code carries a
        // release suffix. Populated by LoadMembers/LoadDimensions; consumed by LoadNewConcepts.
        var releaseCodeByConceptId = new Dictionary<int, string>();

        var metricsDomainId = IdentifyMetricsDomainId(source);
        var metricsDimensionId = IdentifyMetricsDimensionId(source, metricsDomainId);
        var isTypedDomainByDomainId = BuildIsTypedDomainByDomainId(source);

        // OwnerID of Access (1 row, already validated by SkeletonLoader.Load before getting here)
        // for the mConcept row with no source in Concept (the "Open" sentinel member).
        var accessOwnerId = source.ReadOwners().SingleOrDefault()?.OwnerId
            ?? throw new InvalidOperationException(
                "The Access Owner table is empty; exactly 1 row was expected.");
        var reservedConceptIds = new HashSet<int>();
        var sentinelMemberConceptId = DetermineSyntheticConceptId(source, reservedConceptIds);

        // The renumbering of the occupant of MemberID 9999 (if any) is decided ONCE, before
        // loading mMember and mHierarchyNode, and both apply the same translation.
        var memberIdRenumbering = DetermineMemberIdRenumbering(source);

        // MemberID (target) -> MemberLabel, populated by LoadMembers and reused by
        // LoadHierarchyNodes to seed mConceptTranslation (that entity has no HierarchyNodeLabel
        // of its own in Access).
        var memberLabelByDestinationMemberId = new Dictionary<int, string?>();

        var domainRows = LoadDomains(source, destination, dataTypeLabelById, metricsDomainId, reachableConceptIds, translationSeeds);
        var memberRows = LoadMembers(source, destination, reachableConceptIds, translationSeeds, releaseCodeByConceptId, memberIdRenumbering, sentinelMemberConceptId, memberLabelByDestinationMemberId);
        var dimensionRows = LoadDimensions(source, destination, metricsDimensionId, isTypedDomainByDomainId, reachableConceptIds, translationSeeds, releaseCodeByConceptId);
        var metricRows = LoadMetrics(source, destination, dataTypeLabelById, flowTypeLabelById);
        var hierarchyRows = LoadHierarchies(source, destination, reachableConceptIds, translationSeeds);
        var hierarchyNodeRows = LoadHierarchyNodes(source, destination, reachableConceptIds, translationSeeds, memberIdRenumbering, memberLabelByDestinationMemberId);

        var syntheticConcepts = new List<(int ConceptId, string ConceptType)>
        {
            (sentinelMemberConceptId, "Member"),
        };
        var newConceptRows = LoadNewConcepts(source, destination, reachableConceptIds, releaseCodeByConceptId, releaseIdByCode, syntheticConcepts, accessOwnerId);
        var translationRows = LoadConceptTranslations(destination, translationSeeds);

        return new Result(
            DomainRows: domainRows,
            MemberRows: memberRows,
            DimensionRows: dimensionRows,
            MetricRows: metricRows,
            HierarchyRows: hierarchyRows,
            HierarchyNodeRows: hierarchyNodeRows,
            NewConceptRows: newConceptRows,
            ConceptTranslationRows: translationRows,
            MemberIdRenumbering: memberIdRenumbering);
    }
}
