using EbaDpm.Converter.Core.Access.Dpm20;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Mapping.Dpm20;

/// <summary>
/// Resolves, for <see cref="Dpm20AxisAndCellLoader"/>, the destination <c>DimensionID</c>/
/// <c>MemberID</c> of a (property, item) pair of <c>ContextComposition</c>, of an open-axis
/// property (no item: the source itself IS the dimension), or of the metric member of a
/// property (domain <c>MET</c>).
///
/// It deliberately resolves by CODE against the <c>mDomain</c>/<c>mMember</c>/<c>mDimension</c>
/// tables ALREADY WRITTEN by <see cref="Dpm20DictionaryLoader"/> (same connection), instead of
/// reproducing the ID assignment sequence of that class: the result stays correct even if that
/// sequence changes, and there are no two places in the code that could diverge on which
/// <c>DomainID</c>/<c>DimensionID</c>/<c>MemberID</c> corresponds to a given code.
///
/// The only real ambiguity (11 properties with MORE THAN ONE candidate domain at the same time,
/// and the remaining dimension properties with more than one CODE per release window) is resolved
/// as follows: if a context item anchors the property to a specific domain, that domain decides
/// (<see cref="ResolveDimensionForProperty"/> with <c>targetDomainId</c>); otherwise (an open-axis
/// property, with no item), the window in force at the cutoff release is preferred and, if more
/// than one remains, the one with the lowest <c>CategoryID</c> — deterministic, not forced to
/// match any count.
/// </summary>
internal sealed class DimensionMemberResolver
{
    public required int MetDomainId { get; init; }
    public required int CutoffReleaseId { get; init; }

    internal required Dictionary<int, string> CategoryCodeById { get; init; }
    internal required Dictionary<int, List<Dpm20ItemCategoryRow>> ItemCategoriesByItemId { get; init; }
    internal required Dictionary<int, List<Dpm20ItemCategoryRow>> PrItemCodeWindowsByPropertyId { get; init; }
    internal required Dictionary<int, List<Dpm20PropertyCategoryRow>> PropertyCategoriesByPropertyId { get; init; }
    internal required Dictionary<int, string> DataTypeCodeByPropertyId { get; init; }
    internal required Dictionary<string, int> DomainIdByCode { get; init; }
    internal required Dictionary<(int DomainId, string Code), (int MemberId, string? MemberXbrlCode)> MemberByDomainAndCode { get; init; }
    internal required Dictionary<(int DomainId, string Code), (int DimensionId, string? DimensionXbrlCode)> DimensionByDomainAndCode { get; init; }

    /// <summary>
    /// <c>mMember.IsDefaultMember</c> already written by <see cref="Dpm20DictionaryLoader"/>, by
    /// <c>MemberID</c> (step 4 of the signature algorithm: pairs whose member is the default of
    /// its domain are dropped, except the metric). It is deliberately NOT used for the open-axis
    /// pair (sentinel <c>MemberID</c> <c>9999</c>, see <see cref="Dpm20AxisAndCellLoader"/>): that
    /// value does not correspond to any real <c>mMember</c> member — it is a minted literal, not a
    /// <c>MemberID</c> produced by the sequential counter of
    /// <c>Dpm20DictionaryLoader.LoadMembers</c> — and could coincide by accident with the
    /// <c>MemberID</c> of a real member if the total census exceeds 9,999 rows. The open axis is
    /// treated as "never default" by construction (same criterion as DPM 1.0:
    /// <c>IsDefaultMember: false</c> hardcoded for the open-axis pair), not by looking it up in
    /// this dictionary.
    /// </summary>
    internal required Dictionary<int, bool> IsDefaultMemberByMemberId { get; init; }
    internal required int NaCategoryId { get; init; }
    internal required int IntNaDomainId { get; init; }
    internal required int StrNaDomainId { get; init; }

    /// <summary>
    /// The same list received by <see cref="Dpm20AxisAndCellLoader.Load"/> and that ends up in
    /// <c>Result.StructuralAnomalies</c> — a single channel for what is not an error (it does not
    /// abort) but must not be fixed silently either (a recovery rather than a failure).
    /// <see cref="ResolveMemberByDomainAndItem"/> uses it to name the cases in which the domain
    /// anchor discards the window in force at the cutoff.
    /// </summary>
    internal required List<string> StructuralAnomalies { get; init; }

    /// <summary>Diagnostic counters (reported; they neither force nor abort the conversion).</summary>
    public int UnresolvedContextPairs;
    public int UnresolvedMetricPairs;
    public int UnresolvedOpenAxisDimensions;

    /// <summary>
    /// Key headers with a non-null <c>SubCategoryVID</c> whose chain
    /// <c>SubCategoryVID → SubCategoryVersion.SubCategoryID → mHierarchy</c> does NOT resolve
    /// (measured as 0 in the 4.2 source; reported, not forced).
    /// </summary>
    public int UnresolvedOpenAxisRestrictions;

    public static DimensionMemberResolver Build(Dpm20AccessReader reader, SqliteConnection destination, List<string> structuralAnomalies)
    {
        var categories = reader.ReadCategories().ToList();
        var categoryCodeById = categories.ToDictionary(c => c.CategoryId, c => c.Code);

        var prCategoryId = categories.FirstOrDefault(c => c.Code == "_PR")?.CategoryId
            ?? throw new InvalidOperationException("Category '_PR' not found in Category.");
        var naCategoryId = categories.FirstOrDefault(c => c.Code == "_NA")?.CategoryId
            ?? throw new InvalidOperationException("Category '_NA' not found in Category.");

        var itemCategories = reader.ReadItemCategories().ToList();
        var itemCategoriesByItemId = itemCategories
            .GroupBy(ic => ic.ItemId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var prItemCodeWindowsByPropertyId = itemCategories
            .Where(ic => ic.CategoryId == prCategoryId)
            .GroupBy(ic => ic.ItemId) // ItemID = PropertyID in _PR
            .ToDictionary(g => g.Key, g => g.ToList());

        var propertyCategoriesByPropertyId = reader.ReadPropertyCategories()
            .GroupBy(pc => pc.PropertyId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var dataTypeCodeById = reader.ReadDataTypes().ToDictionary(dt => dt.DataTypeId, dt => dt.Code);
        var dataTypeCodeByPropertyId = new Dictionary<int, string>();
        foreach (var property in reader.ReadProperties())
        {
            if (property.DataTypeId is { } dataTypeId && dataTypeCodeById.TryGetValue(dataTypeId, out var code))
            {
                dataTypeCodeByPropertyId[property.PropertyId] = code;
            }
        }

        var domainIdByCode = ReadDestinationDictionary(
            destination, "SELECT \"DomainCode\", \"DomainID\" FROM \"mDomain\"",
            r => (r.GetString(0), r.GetInt32(1)));

        var memberByDomainAndCode = new Dictionary<(int, string), (int, string?)>();
        var isDefaultMemberByMemberId = new Dictionary<int, bool>();
        using (var command = destination.CreateCommand())
        {
            command.CommandText = "SELECT \"DomainID\", \"MemberCode\", \"MemberID\", \"MemberXBRLCode\", \"IsDefaultMember\" FROM \"mMember\"";
            using var readerRows = command.ExecuteReader();
            while (readerRows.Read())
            {
                var domainId = readerRows.GetInt32(0);
                var code = readerRows.IsDBNull(1) ? string.Empty : readerRows.GetString(1);
                var memberId = readerRows.GetInt32(2);
                var xbrl = readerRows.IsDBNull(3) ? null : readerRows.GetString(3);
                memberByDomainAndCode.TryAdd((domainId, code), (memberId, xbrl));
                isDefaultMemberByMemberId[memberId] = !readerRows.IsDBNull(4) && readerRows.GetBoolean(4);
            }
        }

        var dimensionByDomainAndCode = new Dictionary<(int, string), (int, string?)>();
        using (var command = destination.CreateCommand())
        {
            command.CommandText = "SELECT \"DomainID\", \"DimensionCode\", \"DimensionID\", \"DimensionXBRLCode\" FROM \"mDimension\"";
            using var readerRows = command.ExecuteReader();
            while (readerRows.Read())
            {
                var domainId = readerRows.GetInt32(0);
                var code = readerRows.IsDBNull(1) ? string.Empty : readerRows.GetString(1);
                var dimensionId = readerRows.GetInt32(2);
                var xbrl = readerRows.IsDBNull(3) ? null : readerRows.GetString(3);
                dimensionByDomainAndCode.TryAdd((domainId, code), (dimensionId, xbrl));
            }
        }

        if (!domainIdByCode.TryGetValue("MET", out var metDomainId))
        {
            throw new InvalidOperationException(
                "mDomain has no row with DomainCode='MET' (Dpm20DictionaryLoader should have written it).");
        }

        if (!domainIdByCode.TryGetValue("INT_NA", out var intNaDomainId) || !domainIdByCode.TryGetValue("STR_NA", out var strNaDomainId))
        {
            throw new InvalidOperationException("mDomain has no INT_NA/STR_NA (Dpm20DictionaryLoader should have written them).");
        }

        return new DimensionMemberResolver
        {
            MetDomainId = metDomainId,
            CutoffReleaseId = reader.CutoffReleaseId,
            CategoryCodeById = categoryCodeById,
            ItemCategoriesByItemId = itemCategoriesByItemId,
            PrItemCodeWindowsByPropertyId = prItemCodeWindowsByPropertyId,
            PropertyCategoriesByPropertyId = propertyCategoriesByPropertyId,
            DataTypeCodeByPropertyId = dataTypeCodeByPropertyId,
            DomainIdByCode = domainIdByCode,
            MemberByDomainAndCode = memberByDomainAndCode,
            DimensionByDomainAndCode = dimensionByDomainAndCode,
            IsDefaultMemberByMemberId = isDefaultMemberByMemberId,
            NaCategoryId = naCategoryId,
            IntNaDomainId = intNaDomainId,
            StrNaDomainId = strNaDomainId,
            StructuralAnomalies = structuralAnomalies,
        };
    }

    private static Dictionary<TKey, TValue> ReadDestinationDictionary<TKey, TValue>(
        SqliteConnection destination, string sql, Func<SqliteDataReader, (TKey, TValue)> project)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, TValue>();
        using var command = destination.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var (key, value) = project(reader);
            result[key] = value;
        }

        return result;
    }

    /// <summary>The <c>DomainID</c> of a candidate <c>PropertyCategory</c>/<c>ItemCategory</c> row by its <c>CategoryID</c> (with the <c>_NA</c> → INT_NA/STR_NA split).</summary>
    private int? ResolveDomainIdForCategory(int categoryId, int propertyIdForNaSplit)
    {
        if (categoryId == NaCategoryId)
        {
            if (!DataTypeCodeByPropertyId.TryGetValue(propertyIdForNaSplit, out var rawType))
            {
                return null;
            }

            return rawType switch { "i" => IntNaDomainId, "s" => StrNaDomainId, _ => null };
        }

        if (!CategoryCodeById.TryGetValue(categoryId, out var code))
        {
            return null;
        }

        return DomainIdByCode.TryGetValue(code, out var domainId) ? domainId : null;
    }

    /// <summary>
    /// Resolves the dimension of a property. <paramref name="targetDomainId"/> anchors the choice
    /// when the property has more than one candidate domain at the same time (11 cases): the
    /// domain of the context ITEM that accompanies it. Without an anchor (open-axis property, no
    /// item), the window in force at the cutoff release is preferred.
    /// </summary>
    public (int DimensionId, string DimensionXbrlCode, int DomainId)? ResolveDimensionForProperty(int propertyId, int? targetDomainId)
    {
        if (!PropertyCategoriesByPropertyId.TryGetValue(propertyId, out var candidates) || candidates.Count == 0)
        {
            return null;
        }

        Dpm20PropertyCategoryRow? chosen = null;
        if (targetDomainId is { } target)
        {
            chosen = candidates.FirstOrDefault(c => ResolveDomainIdForCategory(c.CategoryId, propertyId) == target);
        }

        chosen ??= candidates.Count == 1
            ? candidates[0]
            : candidates.FirstOrDefault(c => IsInForceAtCutoff(c.StartReleaseId, c.EndReleaseId, CutoffReleaseId))
              ?? candidates.OrderBy(c => c.CategoryId).First();

        var domainId = ResolveDomainIdForCategory(chosen.CategoryId, propertyId);
        if (domainId is null)
        {
            return null;
        }

        if (!PrItemCodeWindowsByPropertyId.TryGetValue(propertyId, out var codeWindows) || codeWindows.Count == 0)
        {
            return null;
        }

        var codeWindow = codeWindows.FirstOrDefault(w =>
                WindowsOverlap(w.StartReleaseId, w.EndReleaseId, chosen.StartReleaseId, chosen.EndReleaseId))
            ?? (codeWindows.Count == 1 ? codeWindows[0] : codeWindows.OrderByDescending(w => w.StartReleaseId).First());

        if (!DimensionByDomainAndCode.TryGetValue((domainId.Value, codeWindow.Code), out var dimension))
        {
            return null;
        }

        return (dimension.Item1, dimension.Item2 ?? codeWindow.Code, domainId.Value);
    }

    /// <summary>The domain of the item of a <c>ContextComposition</c> pair — the anchor for <see cref="ResolveDimensionForProperty"/>.</summary>
    public int? ResolveItemDomainId(int itemId)
    {
        if (!ItemCategoriesByItemId.TryGetValue(itemId, out var windows) || windows.Count == 0)
        {
            return null;
        }

        var chosen = windows.FirstOrDefault(w => IsInForceAtCutoff(w.StartReleaseId, w.EndReleaseId, CutoffReleaseId)) ?? windows[0];
        return ResolveDomainIdForCategory(chosen.CategoryId, itemId);
    }

    /// <summary>
    /// The destination <c>MemberID</c> of a context item, once its domain is known.
    ///
    /// ANCHORS by <paramref name="domainId"/>, like its sibling
    /// <see cref="ResolveDimensionForProperty"/> does with <c>targetDomainId</c>. An item with more
    /// than one category window (recategorized between releases; e.g. in 4.3, ItemID=9865 moves
    /// from <c>CategoryID=370</c>/code <c>x73</c> to another category with code <c>qx2082</c>
    /// exactly at the cutoff) used to pick "the one in force at the cutoff" blindly, even if it
    /// belonged to a different category from the one the caller asks for — the code did not exist
    /// in <c>mMember</c> for that domain and the pair was left unresolved.
    ///
    /// The anchor is NOT "the first window that matches the domain": that broke the COMMON case, a
    /// simple code rename within the SAME domain (a renamed ItemID leaves two rows with different
    /// Code; 5 of 1,084 dimension properties) — with two windows of the SAME category, "anchor by
    /// domain" disambiguates nothing and picking the first one in the list is arbitrary; it could
    /// leave an OLD code where the current one used to come out. The anchor is applied first to
    /// RESTRICT to the windows whose category resolves to the requested domain, and ONLY WITHIN
    /// that subset is the one in force at the cutoff still preferred — so the common case (a
    /// single category, perhaps several codes over time) gives exactly the same result as before,
    /// and the anchor only decides when genuinely different categories (domains) are at play.
    /// </summary>
    public (int MemberId, string? MemberXbrlCode)? ResolveMemberByDomainAndItem(int domainId, int itemId)
    {
        if (!ItemCategoriesByItemId.TryGetValue(itemId, out var windows) || windows.Count == 0)
        {
            return null;
        }

        var byCutoff = windows.FirstOrDefault(w => IsInForceAtCutoff(w.StartReleaseId, w.EndReleaseId, CutoffReleaseId)) ?? windows[0];

        var matchingDomain = windows.Count > 1
            ? windows.Where(w => ResolveDomainIdForCategory(w.CategoryId, itemId) == domainId).ToList()
            : [];
        var anchored = matchingDomain.Count > 0
            ? matchingDomain.FirstOrDefault(w => IsInForceAtCutoff(w.StartReleaseId, w.EndReleaseId, CutoffReleaseId)) ?? matchingDomain[0]
            : null;
        var chosen = anchored ?? byCutoff;

        // Not an error: it is a legitimate recovery, but it must leave a trace instead of being
        // fixed silently (a recovery rather than a failure).
        if (anchored is not null && !Equals(anchored, byCutoff))
        {
            StructuralAnomalies.Add(
                $"ItemID={itemId}: the anchor by DomainID={domainId} discarded the window in force at the cutoff "
                + $"(CategoryID={byCutoff.CategoryId}, Code='{byCutoff.Code}') and used instead "
                + $"CategoryID={anchored.CategoryId}, Code='{anchored.Code}'.");
        }

        return MemberByDomainAndCode.TryGetValue((domainId, chosen.Code), out var member) ? member : null;
    }

    /// <summary>The member of the <c>MET</c> domain for a metric property.</summary>
    public (int MemberId, string? MemberXbrlCode)? ResolveMetMember(int propertyId)
    {
        if (!PrItemCodeWindowsByPropertyId.TryGetValue(propertyId, out var windows) || windows.Count == 0)
        {
            return null;
        }

        var chosen = windows.FirstOrDefault(w => IsInForceAtCutoff(w.StartReleaseId, w.EndReleaseId, CutoffReleaseId))
            ?? (windows.Count == 1 ? windows[0] : null);
        if (chosen is null)
        {
            return null;
        }

        return MemberByDomainAndCode.TryGetValue((MetDomainId, chosen.Code), out var member) ? member : null;
    }

    private static bool IsInForceAtCutoff(int startReleaseId, int? endReleaseId, int cutoff)
        => startReleaseId <= cutoff && (endReleaseId is null || endReleaseId > cutoff);

    /// <summary>A <c>[Start, End)</c> window, with <c>End = null</c> meaning open-ended (same helper as <c>Dpm20DictionaryLoader</c>).</summary>
    private static bool WindowsOverlap(int startA, int? endA, int startB, int? endB)
    {
        var overlapStart = Math.Max(startA, startB);
        var overlapEndExclusive = endA is null ? endB : (endB is null ? endA : Math.Min(endA.Value, endB.Value));
        return overlapEndExclusive is null || overlapStart < overlapEndExclusive.Value;
    }
}
