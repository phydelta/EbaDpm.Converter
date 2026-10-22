namespace EbaDpm.Converter.Core.Mapping.Dpm20;

/// <summary>
/// Pure signature-composition functions of the DPM 2.0 cell loader (<c>BusinessCode</c>,
/// <c>DPS</c>/<c>DMS</c> of a pair and of a full cell): no loader state, so they can be tested
/// directly. Moved out of <see cref="Dpm20AxisAndCellLoader"/> unchanged.
/// </summary>
internal static class Dpm20CellSignatureComposer
{
    private const int MetDimensionId = Dpm20AxisAndCellLoader.MetDimensionId;
    private const int OpenAxisMemberSentinel = Dpm20AxisAndCellLoader.OpenAxisMemberSentinel;

    /// <summary>Y=0, X=1, Z=2 (the usual row, column, sheet convention).</summary>
    internal static int OrientationRank(string orientation) => orientation switch { "Y" => 0, "X" => 1, "Z" => 2, _ => 3 };

    /// <summary>
    /// <c>mTableCell.BusinessCode</c>: <c>{table,ordinates in order Y,X,Z}</c>. The PRIMARY order
    /// is the orientation (100% measured): there are no two ordinates of the same cell with
    /// different orientations to break a tie.
    ///
    /// The SECONDARY order — between several ordinates of the SAME orientation in the SAME cell,
    /// which only occurs with several OPEN axes of that orientation (several key X headers of the
    /// same TableVID, transposed to Y; it never coexists with a CLOSED axis of that same
    /// orientation, 0 measured cases) — has NO rule derivable from a single source column:
    /// <list type="bullet">
    /// <item><description>
    /// It is NOT <c>mTableAxis.Order</c> (measured: it gives the OPPOSITE order to the reference in
    /// <c>K_50.00</c>).
    /// </description></item>
    /// <item><description>
    /// It is NOT the <c>HeaderID</c>/<c>TableVersionHeader.Order</c> of the source key header, in
    /// either direction: in <c>K_50.00</c> (2 axes) descending DOES reproduce the reference, but in
    /// <c>K_43.00.a</c> (3 axes, codes <c>0010</c>/<c>0020</c>/<c>0040</c> with source
    /// <c>Order</c> 1020/1030/1040) the reference gives <c>0040,0010,0020</c> — neither ascending
    /// nor descending by either field.
    /// </description></item>
    /// </list>
    /// Measured on release 4.2 only (not a property of the format), over the 144,463 (table,
    /// BusinessCode) keys of the 4.2 reference: ascending by own
    /// <c>AxisID</c> matches 142,488 (98.63%); descending, 143,280 (99.18%). NEITHER closes at
    /// 100%, and — the proof that there is no rule slipping away — they are NOT a subset of each
    /// other: 927 keys that ascending gets right are lost by descending, and descending wins 1,719
    /// different ones that ascending did not have. That is the sign that the secondary order is NOT
    /// by <c>AxisID</c> in any direction: the reference uses a convention of its own, not derivable
    /// from the source with the available fields.
    ///
    /// That is why DESCENDING is a DECLARED CHOICE, not a derived rule — it is documented as a
    /// divergence from the reference, with the two measured figures, not as "the variant that
    /// matches most". The residue (~1,183 of 144,463 BusinessCode, 0.8%, on release 4.2) stays named, not forced.
    /// </summary>
    internal static string ComposeBusinessCode(
        string tableCode,
        List<int> ordinateIdsOfCell,
        IReadOnlyDictionary<int, (int Rank, int AxisId)> orientationRankAndAxisIdByOrdinateId,
        IReadOnlyDictionary<int, string> ordinateCodeByOrdinateId)
    {
        var codes = ordinateIdsOfCell
            .OrderBy(id => orientationRankAndAxisIdByOrdinateId[id].Rank)
            .ThenByDescending(id => orientationRankAndAxisIdByOrdinateId[id].AxisId)
            .Select(id => ordinateCodeByOrdinateId[id]);

        return "{" + tableCode + "," + string.Join(",", codes) + "}";
    }

    /// <summary>Maximum depth when climbing through <c>ParentOrdinateID</c> composing a signature — the same defensive guard as DPM 1.0 (possible cycle: aborts with a clear message, not silently).</summary>
    private const int MaxOrdinateTreeDepth = 64;

    /// <summary>
    /// <c>mTableCell.DPS</c>/<c>DatapointSignature</c> (the same algorithm as DPM 1.0, not
    /// re-derived): for each ordinate of the cell, it climbs through
    /// <paramref name="parentOrdinateIdByOrdinateId"/> accumulating depth; for each
    /// <c>DimensionID</c>, across ALL the ordinates of the cell at once (not axis by axis), the
    /// pair seen at the LOWEST depth wins. Default members are discarded except the metric (which
    /// always survives, whether or not it is marked default — in 4.0/4.2 ALL its members are).
    /// It composes <c>MET(...)</c> first and the rest ORDINALLY ordered by the dimension's XBRL
    /// code, joined by <c>|</c>, reusing <see cref="ComposeDps"/>/<see cref="ComposeDms"/> — the
    /// same pair-composition rule already used to write <c>mOrdinateCategorisation</c>.
    /// Each winning pair carries its own <c>Restriction</c> (inherited from the ordinate that
    /// contributed it), so <c>Dps</c> and <c>Dms</c> can differ in the bracket of an open axis
    /// WITH a restriction — both are returned, never one derived from the other.
    /// </summary>
    internal static (string? Dps, string? Dms) ComposeCellSignature(
        List<int> ordinateIdsOfCell,
        IReadOnlyDictionary<int, List<(int DimensionId, string DimensionXbrlCode, int MemberId, string? MemberXbrlCode, bool IsDefaultMember, (int HierarchyId, string HierarchyCode)? Restriction)>> ownCategorisationsByOrdinateId,
        IReadOnlyDictionary<int, int?> parentOrdinateIdByOrdinateId)
    {
        var chosen = new Dictionary<int, (int Depth, (int DimensionId, string DimensionXbrlCode, int MemberId, string? MemberXbrlCode, bool IsDefaultMember, (int HierarchyId, string HierarchyCode)? Restriction) Pair)>();

        foreach (var start in ordinateIdsOfCell)
        {
            var depth = 0;
            int? current = start;
            var steps = 0;

            while (current is { } currentId)
            {
                if (++steps > MaxOrdinateTreeDepth)
                {
                    throw new InvalidOperationException(
                        $"The ordinate tree starting at OrdinateID={start} exceeds the maximum " +
                        $"expected depth ({MaxOrdinateTreeDepth}): possible cycle, unforeseen anomaly.");
                }

                if (ownCategorisationsByOrdinateId.TryGetValue(currentId, out var pairs))
                {
                    foreach (var pair in pairs.Where(pair => !chosen.TryGetValue(pair.DimensionId, out var existing) || depth < existing.Depth))
                    {
                        chosen[pair.DimensionId] = (depth, pair);
                    }
                }

                current = parentOrdinateIdByOrdinateId.TryGetValue(currentId, out var parent) ? parent : null;
                depth++;
            }
        }

        // Pairs whose member is the default of its domain are discarded, except the metric, which
        // is never discarded.
        var alive = chosen.Values
            .Where(v => v.Pair.DimensionId == MetDimensionId || !v.Pair.IsDefaultMember)
            .ToList();

        var orderedDps = new List<string>();
        var orderedDms = new List<string>();
        var metricPair = alive.Where(v => v.Pair.DimensionId == MetDimensionId).Select(v => v.Pair).ToList();
        if (metricPair.Count > 0)
        {
            var m = metricPair[0];
            // The metric never carries a bracket (the open-axis sentinel is NEVER the metric
            // dimension, and ComposeDps/ComposeDms resolve it the same way without looking at
            // Restriction), so Dps and Dms of the metric pair are literally the same text.
            orderedDps.Add(ComposeDps(m.DimensionId, m.DimensionXbrlCode, m.MemberId, m.MemberXbrlCode, m.Restriction));
            orderedDms.Add(ComposeDms(m.DimensionId, m.DimensionXbrlCode, m.MemberId, m.MemberXbrlCode, m.Restriction));
        }

        foreach (var v in alive
            .Where(v => v.Pair.DimensionId != MetDimensionId)
            .OrderBy(v => v.Pair.DimensionXbrlCode, StringComparer.Ordinal))
        {
            orderedDps.Add(ComposeDps(v.Pair.DimensionId, v.Pair.DimensionXbrlCode, v.Pair.MemberId, v.Pair.MemberXbrlCode, v.Pair.Restriction));
            orderedDms.Add(ComposeDms(v.Pair.DimensionId, v.Pair.DimensionXbrlCode, v.Pair.MemberId, v.Pair.MemberXbrlCode, v.Pair.Restriction));
        }

        var dps = orderedDps.Count > 0 ? string.Join("|", orderedDps) : null;
        var dms = orderedDms.Count > 0 ? string.Join("|", orderedDms) : null;
        return (dps, dms);
    }

    /// <summary>
    /// A dimension (the metric included) contributed to ONE cell by ordinates of more than one
    /// axis. <see cref="ByAxisId"/> holds, per axis, the codes of the declaring ordinates and the
    /// member codes declared there (the open-axis sentinel is shown as <c>*</c>).
    /// </summary>
    internal sealed record CrossAxisDimension(
        int DimensionId,
        string DimensionXbrlCode,
        SortedDictionary<int, AxisContribution> ByAxisId);

    /// <summary>What ONE axis contributes of a dimension: the codes of the ordinates that declare it and the member codes.</summary>
    internal sealed record AxisContribution(SortedSet<string> OrdinateCodes, SortedSet<string> Members);

    /// <summary>
    /// Structural check of a cell signature: a cell has one signature, so no dimension may be
    /// declared by ordinates of two or more distinct axes. Every pair that
    /// <see cref="ComposeCellSignature"/> considers (own and inherited, open-axis and fixed pairs)
    /// is attributed to the axis of the cell ordinate it was reached from, BEFORE the default
    /// discard and whatever the depth or whether the members agree. Within ONE axis an ordinate
    /// overriding an inherited pair is legitimate and never reported. Pure: it does not change
    /// what <see cref="ComposeCellSignature"/> returns.
    /// </summary>
    internal static IReadOnlyList<CrossAxisDimension> FindCrossAxisDimensions(
        List<int> ordinateIdsOfCell,
        IReadOnlyDictionary<int, List<(int DimensionId, string DimensionXbrlCode, int MemberId, string? MemberXbrlCode, bool IsDefaultMember, (int HierarchyId, string HierarchyCode)? Restriction)>> ownCategorisationsByOrdinateId,
        IReadOnlyDictionary<int, int?> parentOrdinateIdByOrdinateId,
        IReadOnlyDictionary<int, (int Rank, int AxisId)> orientationRankAndAxisIdByOrdinateId,
        IReadOnlyDictionary<int, string> ordinateCodeByOrdinateId)
    {
        // Pass 1: first axis seen per dimension; flag the dimensions seen from a second axis.
        Dictionary<int, int>? firstAxisByDimension = null;
        HashSet<int>? conflicting = null;

        foreach (var (start, _, current) in EnumerateReachablePairs(ordinateIdsOfCell, ownCategorisationsByOrdinateId, parentOrdinateIdByOrdinateId))
        {
            var axisId = orientationRankAndAxisIdByOrdinateId[start].AxisId;
            firstAxisByDimension ??= [];
            if (!firstAxisByDimension.TryAdd(current.DimensionId, axisId) && firstAxisByDimension[current.DimensionId] != axisId)
            {
                (conflicting ??= []).Add(current.DimensionId);
            }
        }

        if (conflicting is null)
        {
            return [];
        }

        // Pass 2 (rare): the detail of the conflicting dimensions only.
        var byDimension = new SortedDictionary<string, CrossAxisDimension>(StringComparer.Ordinal);
        foreach (var (start, declaring, pair) in EnumerateReachablePairs(ordinateIdsOfCell, ownCategorisationsByOrdinateId, parentOrdinateIdByOrdinateId))
        {
            if (!conflicting.Contains(pair.DimensionId))
            {
                continue;
            }

            if (!byDimension.TryGetValue(pair.DimensionXbrlCode, out var entry))
            {
                entry = new CrossAxisDimension(pair.DimensionId, pair.DimensionXbrlCode, []);
                byDimension[pair.DimensionXbrlCode] = entry;
            }

            var axisId = orientationRankAndAxisIdByOrdinateId[start].AxisId;
            if (!entry.ByAxisId.TryGetValue(axisId, out var contribution))
            {
                contribution = new AxisContribution(new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
                entry.ByAxisId[axisId] = contribution;
            }

            contribution.OrdinateCodes.Add(ordinateCodeByOrdinateId[declaring]);
            contribution.Members.Add(pair.MemberId == OpenAxisMemberSentinel ? "*" : pair.MemberXbrlCode ?? $"MemberID={pair.MemberId}");
        }

        return byDimension.Values.ToList();
    }

    /// <summary>Every (cell ordinate, pair) reachable from the cell ordinates through <c>ParentOrdinateID</c>, same walk and depth guard as <see cref="ComposeCellSignature"/>.</summary>
    private static IEnumerable<(int Start, int Declaring, (int DimensionId, string DimensionXbrlCode, int MemberId, string? MemberXbrlCode, bool IsDefaultMember, (int HierarchyId, string HierarchyCode)? Restriction) Pair)> EnumerateReachablePairs(
        List<int> ordinateIdsOfCell,
        IReadOnlyDictionary<int, List<(int DimensionId, string DimensionXbrlCode, int MemberId, string? MemberXbrlCode, bool IsDefaultMember, (int HierarchyId, string HierarchyCode)? Restriction)>> ownCategorisationsByOrdinateId,
        IReadOnlyDictionary<int, int?> parentOrdinateIdByOrdinateId)
    {
        foreach (var start in ordinateIdsOfCell)
        {
            int? current = start;
            var steps = 0;

            while (current is { } currentId)
            {
                if (++steps > MaxOrdinateTreeDepth)
                {
                    throw new InvalidOperationException(
                        $"The ordinate tree starting at OrdinateID={start} exceeds the maximum " +
                        $"expected depth ({MaxOrdinateTreeDepth}): possible cycle, unforeseen anomaly.");
                }

                if (ownCategorisationsByOrdinateId.TryGetValue(currentId, out var pairs))
                {
                    foreach (var pair in pairs)
                    {
                        yield return (start, currentId, pair);
                    }
                }

                current = parentOrdinateIdByOrdinateId.TryGetValue(currentId, out var parent) ? parent : null;
            }
        }
    }

    /// <summary>
    /// The <b>DPS</b> signature (XBRL codes) of ONE PAIR — applied here only at PAIR level; the
    /// composition of a full cell, with inheritance and Y/X/Z order, is done by
    /// <see cref="ComposeCellSignature"/>: metric first (<c>MET(memberXbrl)</c>), open-axis
    /// sentinel — without restriction, <c>dimXbrl(*)</c>; WITH restriction,
    /// <c>dimXbrl(*[HierarchyCode])</c> — or the normal closed pair (<c>dimXbrl(memberXbrl)</c>).
    /// <paramref name="restriction"/> is only looked at when <paramref name="memberId"/> is the
    /// open-axis sentinel (the metric never produces a bracket even if a metric categorisation came
    /// with a restriction).
    /// </summary>
    internal static string ComposeDps(
        int dimensionId, string dimensionXbrlCode, int memberId, string? memberXbrlCode,
        (int HierarchyId, string HierarchyCode)? restriction = null)
    {
        if (dimensionId == MetDimensionId)
        {
            return $"MET({memberXbrlCode})";
        }

        if (memberId == OpenAxisMemberSentinel)
        {
            return restriction is { } r ? $"{dimensionXbrlCode}(*[{r.HierarchyCode}])" : $"{dimensionXbrlCode}(*)";
        }

        return $"{dimensionXbrlCode}({memberXbrlCode})";
    }

    /// <summary>
    /// The <b>DMS</b> signature (database IDs) of ONE PAIR — same case as
    /// <see cref="ComposeDps"/> and DIFFERENT only in the bracket of the open-axis sentinel WITH a
    /// restriction: <c>dimXbrl(*[HierarchyID])</c> instead of <c>dimXbrl(*[HierarchyCode])</c>.
    /// Outside that case, <c>Dps</c> and <c>Dms</c> are the same text (same invariant as DPM 1.0).
    /// </summary>
    internal static string ComposeDms(
        int dimensionId, string dimensionXbrlCode, int memberId, string? memberXbrlCode,
        (int HierarchyId, string HierarchyCode)? restriction = null)
    {
        if (dimensionId == MetDimensionId)
        {
            return $"MET({memberXbrlCode})";
        }

        if (memberId == OpenAxisMemberSentinel)
        {
            return restriction is { } r ? $"{dimensionXbrlCode}(*[{r.HierarchyId}])" : $"{dimensionXbrlCode}(*)";
        }

        return $"{dimensionXbrlCode}({memberXbrlCode})";
    }
}
