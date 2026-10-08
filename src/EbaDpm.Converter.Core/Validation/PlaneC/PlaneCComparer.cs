using EbaDpm.Converter.Core.Layouts;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.PlaneC;

public enum PlaneCViolationType
{
    /// <summary>The layout mentions a dimension with ANOTHER member than ours. The real finding.</summary>
    Contradicts,

    /// <summary>No signature of ours contains the layout one.</summary>
    NoMatch,
}

/// <summary>Table class, measured per <c>TableID</c>, never per <c>TableCode</c> (grouping by
/// code before classifying manufactures a fourth class that does not exist: 846 rows over 788
/// codes). Open if any axis of the table has <c>IsOpenAxis=1</c>; otherwise real Z if any Z axis has
/// more than one ordinate; otherwise REGULAR.</summary>
public enum PlaneCTableClass
{
    Regular,
    OpenAxis,
    ZReal,
}

public sealed record PlaneCViolation(string TableCode, PlaneCViolationType Type, string CanonicalSignature, PlaneCTableClass TableClass)
{
    /// <summary>
    /// The business key of the violation: <c>(TableCode, LayoutSignature)</c>, WITHOUT the type.
    /// <c>contradicts</c>/<c>no-match</c> is a DIAGNOSIS, not identity: the finding is the same
    /// object (that layout signature is not contained) whichever way it is classified, and
    /// classifying it is a measurement of THIS code, not of the script that originally wrote
    /// <c>plane-c-known-divergences-4.2.tsv</c>.
    /// </summary>
    public string BusinessKey => $"{TableCode}\t{CanonicalSignature}";

    public string TypeText => Type == PlaneCViolationType.Contradicts ? "contradicts" : "no-match";
}

public sealed record PlaneCExtraTerm(string TableCode, string Term)
{
    public string BusinessKey => $"{TableCode}|{Term}";
}

/// <summary>Figures of one table class (REGULAR / open axis / real Z); same columns as the global
/// coverage, so that the per-class breakdown can be verified.</summary>
public sealed record PlaneCClassStats(long Tables, long SignaturesCompared, long SignaturesContained);

/// <summary>The examined units ALWAYS come first, with each exclusion reason named.</summary>
public sealed record PlaneCCoverage(
    long TablesCompared,
    long SignaturesCompared,
    long SignaturesContained,
    long TablesSkippedNoMatchingLayoutSheet,
    long TablesSkippedNoOutputRows,
    long LayoutTableCodesWithoutGeneratedTable,
    long LayoutCellsExamined,
    long LayoutCellsWithEmptySignature,
    // The REL.n -> REL tolerance is NAMED, never silent.
    long SheetsMatchedByPointReleaseTolerance,
    IReadOnlyList<string> PointReleaseToleranceSamples,
    IReadOnlyDictionary<PlaneCTableClass, PlaneCClassStats> ByClass,
    // The unit of comparison is the table CODE, not the TableID: a code with more than one TableID
    // (the same table in two taxonomies) has a SINGLE layout sheet, so comparing it twice would
    // count the same evidence twice.
    long TableCodesWithMultipleTableIds,
    long TableCodesWithMultipleTableIdsAndIdenticalSignatures,
    long TableCodesWithInconsistentClass,
    // Signatures whose only MET(m) term names a dimension that the signature itself carries as an
    // open axis m(*): the KEY COLUMN of an open table, drawn by the layout as one more cell of the
    // grid. Outside the compared population (neither contained nor a violation), but NAMED: how
    // many and in which tables, never silently.
    long SignaturesExcludedKeyColumn,
    IReadOnlyList<string> SignaturesExcludedKeyColumnSamples);

public sealed record PlaneCResult(
    IReadOnlyList<PlaneCViolation> Violations,
    IReadOnlyList<PlaneCExtraTerm> ExtraTerms,
    PlaneCCoverage Coverage);

/// <summary>
/// The plane C comparer: compares TABLE by TABLE, by table CODE (the layout publishes ONE sheet
/// per code, not one per taxonomy; counting the same code twice via two different <c>TableID</c>
/// values would contrast the same evidence twice against the same sheet: 58 codes have more than
/// one <c>TableID</c>, all 58 with an identical signature set after normalization, which produced
/// 6,696 signatures counted twice with the old unit). The set of signatures rebuilt from the layout
/// is compared against the UNION of the signatures of all the <c>TableID</c> values that share that
/// code (<c>mTableCell.DatapointSignature</c>), with a CONTAINMENT criterion, never set equality.
///
/// The table class (REGULAR/open/real Z) is still measured per <c>TableID</c> and AGGREGATED by
/// code: the <c>TableID</c> values of one code must agree on class (it is counted and reported if
/// they do not, never guessed).
///
/// Pairing table to sheet(s) requires the SAME release: it is resolved through
/// <c>mTaxonomyTable -> mTaxonomy.TaxonomyCode -> mReportingFramework.FrameworkCode</c>
/// (<c>TaxonomyCode</c> = <c>"{framework in lower case} {release}"</c> by construction in DPM 2.0,
/// see <c>Dpm20TaxonomyDeriver</c>), never from data derived from the Access. A layout release
/// <c>REL.n</c> (point release/hotfix) counts as the own release of a table with release
/// <c>REL</c>, for ANY framework, and is always reported through
/// <see cref="PlaneCCoverage.SheetsMatchedByPointReleaseTolerance"/>.
///
/// The pairing mechanism (<see cref="PlaneCReleaseMatcher.ClassifyRelease"/> and the loading of
/// per-table release keys) lives in <see cref="PlaneCReleaseMatcher"/>, SHARED with
/// <see cref="PlaneCEqualityChecker"/>, so the scope guard of <c>C-EQU-01</c> cannot be left
/// without a criterion of its own.
/// </summary>
public static class PlaneCComparer
{
    public static PlaneCResult Compare(SqliteConnection generated, SqliteConnection layouts)
    {
        var layoutData = PlaneCLayoutReader.Read(layouts);

        var tableCodeByTableId = new Dictionary<long, string>();
        using (var cmd = generated.CreateCommand())
        {
            cmd.CommandText = """SELECT "TableID", "TableCode" FROM "mTable" WHERE "TableCode" IS NOT NULL""";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // Explicitly symmetric with LayoutSheetNameParser: the layout side already arrives
                // canonical from the repository, and this side has to go through the SAME function
                // even though today it is a no-op (0 codes with a space measured in the DPM 1.0
                // output). This avoids manufacturing an asymmetric normalizer.
                tableCodeByTableId[reader.GetInt64(0)] = LayoutTableCodeNormalizer.Canonicalize(reader.GetString(1));
            }
        }

        var tableClassByTableId = ClassifyTables(generated, tableCodeByTableId.Keys);

        var ourSignaturesByTableId = new Dictionary<long, List<HashSet<string>>>();
        using (var cmd = generated.CreateCommand())
        {
            cmd.CommandText = """SELECT "TableID", "DPS" FROM "mTableCell" WHERE "DPS" IS NOT NULL""";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var tableId = reader.GetInt64(0);
                var dps = reader.GetString(1);
                var terms = PlaneCSignature.SplitAndNormalize(dps);
                if (terms.Count == 0)
                {
                    continue;
                }

                if (!ourSignaturesByTableId.TryGetValue(tableId, out var list))
                {
                    list = [];
                    ourSignaturesByTableId[tableId] = list;
                }

                list.Add(new HashSet<string>(terms, StringComparer.Ordinal));
            }
        }

        // TableID -> set of (FrameworkCode in upper case, release) it belongs to, via
        // TaxonomyCode = "{framework in lower case} {release}". Shared with PlaneCEqualityChecker in
        // PlaneCReleaseMatcher.
        var releaseKeysByTableId = PlaneCReleaseMatcher.LoadReleaseKeysByTableId(generated);

        // Layout signatures grouped by TableCode, each with the (framework, release) label of its
        // source sheet.
        var layoutByTableCode = new Dictionary<string, List<LayoutCellSignature>>(StringComparer.Ordinal);
        foreach (var sig in layoutData.Signatures)
        {
            if (!layoutByTableCode.TryGetValue(sig.TableCode, out var list))
            {
                list = [];
                layoutByTableCode[sig.TableCode] = list;
            }

            list.Add(sig);
        }

        var violations = new List<PlaneCViolation>();
        var extraTerms = new List<PlaneCExtraTerm>();
        long tablesCompared = 0;
        long signaturesCompared = 0;
        long signaturesContained = 0;
        long tablesSkippedNoMatchingLayoutSheet = 0;
        long tablesSkippedNoOutputRows = 0;

        var toleratedSheetIds = new HashSet<long>();
        var toleratedSamples = new List<string>();

        var byClassTables = new Dictionary<PlaneCTableClass, long>();
        var byClassSignaturesCompared = new Dictionary<PlaneCTableClass, long>();
        var byClassSignaturesContained = new Dictionary<PlaneCTableClass, long>();
        foreach (PlaneCTableClass cls in Enum.GetValues<PlaneCTableClass>())
        {
            byClassTables[cls] = 0;
            byClassSignaturesCompared[cls] = 0;
            byClassSignaturesContained[cls] = 0;
        }

        long codesWithMultipleTableIds = 0;
        long codesWithMultipleTableIdsAndIdenticalSignatures = 0;
        long codesWithInconsistentClass = 0;

        // The key column of the open axis, counted and named per table.
        long signaturesExcludedKeyColumn = 0;
        var keyColumnExclusionsByTable = new Dictionary<string, long>(StringComparer.Ordinal);

        // The unit is the CODE: a TableCode can live in several TableIDs (the same physical table
        // reused across taxonomies), and the layout only publishes ONE sheet per code. It is grouped
        // here, once, before comparing anything.
        var tableIdsByCode = tableCodeByTableId
            .GroupBy(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var (tableCode, tableIds) in tableIdsByCode)
        {
            if (!layoutByTableCode.TryGetValue(tableCode, out var candidateSignatures))
            {
                // There is not even a sheet with that TableCode in the layouts repository. It does
                // not count as "no layout sheet of ITS release" (that is another cause); there is
                // simply nothing to compare this table with. It is not tallied here: the "table
                // without output" on the opposite side already covers the TableCode mismatch.
                continue;
            }

            if (tableIds.Count > 1)
            {
                codesWithMultipleTableIds++;
                var signatureSetsPerTableId = tableIds
                    .Select(id => ourSignaturesByTableId.GetValueOrDefault(id, [])
                        .Select(PlaneCSignature.Canonicalize)
                        .ToHashSet(StringComparer.Ordinal))
                    .ToList();
                if (signatureSetsPerTableId.All(s => s.SetEquals(signatureSetsPerTableId[0])))
                {
                    codesWithMultipleTableIdsAndIdenticalSignatures++;
                }
            }

            var classesForCode = tableIds
                .Select(id => tableClassByTableId.GetValueOrDefault(id, PlaneCTableClass.Regular))
                .Distinct()
                .ToList();
            if (classesForCode.Count > 1)
            {
                codesWithInconsistentClass++;
            }

            var tableClass = classesForCode[0];

            // Union of the own releases of ALL the TableIDs of this code: the same code can live in
            // taxonomies of the same release published through different paths.
            var releaseKeys = new HashSet<(string Framework, string Release)>();
            foreach (var id in tableIds)
            {
                if (releaseKeysByTableId.TryGetValue(id, out var set))
                {
                    releaseKeys.UnionWith(set);
                }
            }

            var ownRelease = new List<LayoutCellSignature>();
            if (releaseKeys.Count > 0)
            {
                foreach (var s in candidateSignatures)
                {
                    var match = PlaneCReleaseMatcher.ClassifyRelease(releaseKeys, s.FrameworkCode, s.ReleaseLabel);
                    if (match == PlaneCReleaseMatch.None)
                    {
                        continue;
                    }

                    ownRelease.Add(s);
                    if (match == PlaneCReleaseMatch.PointRelease && toleratedSheetIds.Add(s.SheetId))
                    {
                        if (toleratedSamples.Count < 50)
                        {
                            toleratedSamples.Add($"{tableCode} (sheet release='{s.ReleaseLabel}')");
                        }
                    }
                }
            }

            if (ownRelease.Count == 0)
            {
                tablesSkippedNoMatchingLayoutSheet++;
                continue;
            }

            // Union of our signatures from ALL the TableIDs of this code (measured: for the 58
            // duplicated codes, that union is identical to any of its parts).
            var ourSignatures = tableIds
                .SelectMany(id => ourSignaturesByTableId.GetValueOrDefault(id, []))
                .ToList();

            if (ourSignatures.Count == 0)
            {
                tablesSkippedNoOutputRows++;
                continue;
            }

            tablesCompared++;
            byClassTables[tableClass]++;

            // Inverted indexes over OUR signatures of this table: term -> indexes, and
            // dimension -> indexes (to tell "no-match" from "contradicts" without brute force
            // O(layout signatures x our signatures)).
            var distinctOurSignatures = ourSignatures
                .Select(f => (Canonical: PlaneCSignature.Canonicalize(f), Terms: f))
                .GroupBy(x => x.Canonical, StringComparer.Ordinal)
                .Select(g => g.First().Terms)
                .ToList();

            var termIndex = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var dimIndex = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var ourTermUniverse = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < distinctOurSignatures.Count; i++)
            {
                foreach (var term in distinctOurSignatures[i])
                {
                    ourTermUniverse.Add(term);
                    if (!termIndex.TryGetValue(term, out var list))
                    {
                        list = [];
                        termIndex[term] = list;
                    }

                    list.Add(i);

                    var dim = PlaneCSignature.DimensionOf(term);
                    if (!dimIndex.TryGetValue(dim, out var dimList))
                    {
                        dimList = [];
                        dimIndex[dim] = dimList;
                    }

                    if (dimList.Count == 0 || dimList[^1] != i)
                    {
                        dimList.Add(i);
                    }
                }
            }

            var layoutTermUniverse = new HashSet<string>(StringComparer.Ordinal);
            var seenLayoutSignatures = new HashSet<string>(StringComparer.Ordinal);

            foreach (var layoutSignature in ownRelease)
            {
                foreach (var term in layoutSignature.NormalizedTerms)
                {
                    layoutTermUniverse.Add(term);
                }

                var canonical = PlaneCSignature.Canonicalize(layoutSignature.NormalizedTerms);
                if (!seenLayoutSignatures.Add(canonical))
                {
                    continue; // duplicate signature within this table's universe - already evaluated.
                }

                if (IsOpenAxisKeyColumnSignature(layoutSignature.NormalizedTerms))
                {
                    // The layout draws the open axis key as one more grid column (Main Property =
                    // the dimension itself); in the destination that key IS the axis, not a cell, so
                    // there is no possible counterpart. It stays out of the compared population
                    // (neither contained nor a violation), but counted and named.
                    signaturesExcludedKeyColumn++;
                    keyColumnExclusionsByTable[tableCode] = keyColumnExclusionsByTable.GetValueOrDefault(tableCode) + 1;
                    continue;
                }

                signaturesCompared++;
                byClassSignaturesCompared[tableClass]++;

                if (IsContained(layoutSignature.NormalizedTerms, termIndex))
                {
                    signaturesContained++;
                    byClassSignaturesContained[tableClass]++;
                    continue;
                }

                var dims = layoutSignature.NormalizedTerms.Select(PlaneCSignature.DimensionOf).Distinct(StringComparer.Ordinal);
                var hasStructuralCandidate = HasCommonCandidate(dims, dimIndex);
                var type = hasStructuralCandidate ? PlaneCViolationType.Contradicts : PlaneCViolationType.NoMatch;
                violations.Add(new PlaneCViolation(tableCode, type, canonical, tableClass));
            }

            foreach (var term in ourTermUniverse)
            {
                if (!layoutTermUniverse.Contains(term))
                {
                    extraTerms.Add(new PlaneCExtraTerm(tableCode, term));
                }
            }
        }

        var generatedTableCodes = new HashSet<string>(tableCodeByTableId.Values, StringComparer.Ordinal);
        var layoutTableCodesWithoutGeneratedTable = layoutByTableCode.Keys
            .Count(code => !generatedTableCodes.Contains(code));

        var byClass = Enum.GetValues<PlaneCTableClass>().ToDictionary(
            cls => cls,
            cls => new PlaneCClassStats(byClassTables[cls], byClassSignaturesCompared[cls], byClassSignaturesContained[cls]));

        var keyColumnExclusionSamples = keyColumnExclusionsByTable
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key} ({kv.Value})")
            .ToList();

        var coverage = new PlaneCCoverage(
            tablesCompared,
            signaturesCompared,
            signaturesContained,
            tablesSkippedNoMatchingLayoutSheet,
            tablesSkippedNoOutputRows,
            layoutTableCodesWithoutGeneratedTable,
            layoutData.NonShadedCellsExamined,
            layoutData.CellsWithEmptySignature,
            toleratedSheetIds.Count,
            toleratedSamples,
            byClass,
            codesWithMultipleTableIds,
            codesWithMultipleTableIdsAndIdenticalSignatures,
            codesWithInconsistentClass,
            signaturesExcludedKeyColumn,
            keyColumnExclusionSamples);

        return new PlaneCResult(violations, extraTerms, coverage);
    }

    /// <summary>
    /// Table class, per <c>TableID</c> (grouping by <c>TableCode</c> before classifying manufactures
    /// a fourth class that does not exist: a code lives in several taxonomies). Open if any axis of
    /// the table has <c>IsOpenAxis=1</c>; otherwise real Z if any <c>Z</c> axis has more than one
    /// ordinate; otherwise REGULAR.
    /// </summary>
    private static Dictionary<long, PlaneCTableClass> ClassifyTables(SqliteConnection generated, IEnumerable<long> tableIds)
    {
        var idSet = tableIds.ToHashSet();
        var result = new Dictionary<long, PlaneCTableClass>();
        if (idSet.Count == 0)
        {
            return result;
        }

        var hasOpenAxis = new HashSet<long>();
        var zOrdinateCounts = new Dictionary<long, Dictionary<long, long>>(); // TableID -> AxisID(Z) -> count ordinates

        using (var cmd = generated.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT ta."TableID", a."AxisID", a."AxisOrientation", a."IsOpenAxis"
                FROM "mTableAxis" ta
                JOIN "mAxis" a ON a."AxisID" = ta."AxisID"
                """;
            using var reader = cmd.ExecuteReader();
            var axisOrientationById = new Dictionary<long, (long TableId, string? Orientation, bool IsOpen)>();
            while (reader.Read())
            {
                var tableId = reader.GetInt64(0);
                if (!idSet.Contains(tableId))
                {
                    continue;
                }

                var axisId = reader.GetInt64(1);
                var orientation = reader.IsDBNull(2) ? null : reader.GetString(2);
                var isOpen = !reader.IsDBNull(3) && reader.GetInt64(3) != 0;

                if (isOpen)
                {
                    hasOpenAxis.Add(tableId);
                }

                if (string.Equals(orientation, "Z", StringComparison.OrdinalIgnoreCase))
                {
                    if (!zOrdinateCounts.TryGetValue(tableId, out var byAxis))
                    {
                        byAxis = [];
                        zOrdinateCounts[tableId] = byAxis;
                    }

                    byAxis.TryAdd(axisId, 0);
                }

                axisOrientationById[axisId] = (tableId, orientation, isOpen);
            }
        }

        if (zOrdinateCounts.Count > 0)
        {
            var zAxisIds = zOrdinateCounts.Values.SelectMany(d => d.Keys).ToHashSet();
            using var cmd = generated.CreateCommand();
            cmd.CommandText = """SELECT "AxisID", COUNT(*) FROM "mAxisOrdinate" GROUP BY "AxisID" """;
            using var reader = cmd.ExecuteReader();
            var countByAxis = new Dictionary<long, long>();
            while (reader.Read())
            {
                var axisId = reader.GetInt64(0);
                if (zAxisIds.Contains(axisId))
                {
                    countByAxis[axisId] = reader.GetInt64(1);
                }
            }

            foreach (var (tableId, byAxis) in zOrdinateCounts)
            {
                foreach (var axisId in byAxis.Keys.ToList())
                {
                    byAxis[axisId] = countByAxis.GetValueOrDefault(axisId, 0);
                }
            }
        }

        foreach (var tableId in idSet)
        {
            if (hasOpenAxis.Contains(tableId))
            {
                result[tableId] = PlaneCTableClass.OpenAxis;
            }
            else if (zOrdinateCounts.TryGetValue(tableId, out var byAxis) && byAxis.Values.Any(c => c > 1))
            {
                result[tableId] = PlaneCTableClass.ZReal;
            }
            else
            {
                result[tableId] = PlaneCTableClass.Regular;
            }
        }

        return result;
    }

    /// <summary>
    /// Derived rule, no list: a layout signature whose ONLY metric term <c>MET(m)</c> names a
    /// dimension that THAT SAME signature carries as an open axis <c>m(*)</c> is the key column of
    /// an open table. The layout draws it as one more grid column (<c>N_02.00</c>: column
    /// <c>0010</c> "Insolvency ranking", <c>Main Property = qFAB</c>, signature
    /// <c>MET(qFAB)|qFAB(*)</c>). In the destination that key is not a cell: IT IS THE AXIS (same
    /// code and label, the same transposition that the reference database makes), so there is no
    /// counterpart to compare. "Only metric term": if the signature has 0 or more than 1
    /// <c>MET(...)</c> term, the rule does NOT apply (defensive; nothing is guessed on a shape that
    /// has not been measured).
    /// </summary>
    private static bool IsOpenAxisKeyColumnSignature(IReadOnlyList<string> normalizedTerms)
    {
        string? metMember = null;
        var metTermCount = 0;
        foreach (var term in normalizedTerms)
        {
            if (PlaneCSignature.DimensionOf(term) != "MET")
            {
                continue;
            }

            metTermCount++;
            metMember = PlaneCSignature.MemberOf(term);
        }

        if (metTermCount != 1 || string.IsNullOrEmpty(metMember))
        {
            return false;
        }

        var openAxisSentinel = metMember + "(*)";
        foreach (var term in normalizedTerms)
        {
            if (string.Equals(term, openAxisSentinel, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsContained(IReadOnlyList<string> layoutTerms, Dictionary<string, List<int>> termIndex)
    {
        List<int>? current = null;
        foreach (var term in layoutTerms)
        {
            if (!termIndex.TryGetValue(term, out var candidates))
            {
                return false;
            }

            current = current is null ? candidates : Intersect(current, candidates);
            if (current.Count == 0)
            {
                return false;
            }
        }

        return current is { Count: > 0 };
    }

    private static bool HasCommonCandidate(IEnumerable<string> dims, Dictionary<string, List<int>> dimIndex)
    {
        List<int>? current = null;
        foreach (var dim in dims)
        {
            if (!dimIndex.TryGetValue(dim, out var candidates))
            {
                return false;
            }

            current = current is null ? candidates : Intersect(current, candidates);
            if (current.Count == 0)
            {
                return false;
            }
        }

        return current is { Count: > 0 };
    }

    private static List<int> Intersect(List<int> a, List<int> b)
    {
        var setB = new HashSet<int>(b);
        return a.Where(setB.Contains).ToList();
    }
}
