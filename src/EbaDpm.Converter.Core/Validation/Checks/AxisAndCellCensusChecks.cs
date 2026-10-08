using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.Checks;

/// <summary>
/// B-AXE-*, B-CEL-*, B-OCA-01, B-OAR-01: axes, ordinates, cells, positions,
/// categorisation and open-axis restrictions, by business key, against the
/// comparable universe. Generalised to any reference.
///
/// B-OCA-01 also receives the <see cref="ValidationSourceModel"/>: it is a critical check restated
/// per scope, following the same pattern as the other restated checks. Its real criterion is
/// COVERAGE, and in DPM 2.0 equality no longer holds by construction, whereas in DPM 1.0 it
/// remains a measured property of that output.
/// </summary>
public static class AxisAndCellCensusChecks
{
    public static IEnumerable<CheckResult> Run(
        SqliteConnection generated, SqliteConnection reference, string referenceRole, IReadOnlySet<string> taxonomies,
        List<KnownExceptions.Outcome> exceptionSink, ValidationSourceModel model)
    {
        var tableIdsGenerated = BusinessKeys.BuildTableIdsByBusinessKey(generated, taxonomies);
        var tableIdsReference = BusinessKeys.BuildTableIdsByBusinessKey(reference, taxonomies);
        var genTableIdSet = tableIdsGenerated.Values.ToHashSet();
        var refTableIdSet = tableIdsReference.Values.ToHashSet();

        // ---- B-AXE-02: coverage of closed ordinates by OrdinateCode path ----
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var generatedPaths = BusinessKeys.BuildClosedOrdinatePathIndex(generated, genTableIdSet);
        var referencePaths = BusinessKeys.BuildClosedOrdinatePathIndex(reference, refTableIdSet);
        var generatedPathKeys = PathKeysByBusinessTable(generatedPaths, tableIdsGenerated);
        var referencePathKeys = PathKeysByBusinessTable(referencePaths, tableIdsReference);
        yield return PlaneBSupport.Containment(
            "B-AXE-02", ValidationLayer.Critical, "mAxisOrdinate",
            "OrdinateCode: exact match by closed-ordinate path (the most sensitive criterion of the project)",
            "B-AXE-02-ORDINATE-PATH", referenceRole, generatedPathKeys, referencePathKeys, exceptionSink, sw.ElapsedMilliseconds);

        // ---- B-AXE-03: AxisOrientation/IsOpenAxis per table ----
        sw.Restart();
        var generatedAxes = AxisSignatures(generated, taxonomies);
        var referenceAxes = AxisSignatures(reference, taxonomies);
        yield return PlaneBSupport.Containment(
            "B-AXE-03", ValidationLayer.Critical, "mAxis", "AxisOrientation/IsOpenAxis per table: generated contains the reference",
            "B-AXE-03-AXIS-SIGNATURE", referenceRole, generatedAxes, referenceAxes, exceptionSink, sw.ElapsedMilliseconds);

        // ---- B-AXE-04: AxisCode of open axes, literal. Skipped against 3.2: it has no such column ----
        sw.Restart();
        if (!SqlHelpers.ColumnExists(reference, "mAxis", "AxisCode"))
        {
            yield return CheckResult.Skip(
                "B-AXE-04", ValidationPlane.B, ValidationLayer.Critical, "mAxis",
                "AxisCode of open axes: literal against the reference",
                $"Reference '{referenceRole}' does not have the column mAxis.AxisCode (3.2 predates this column of the format)");
        }
        else
        {
            var generatedOpenAxisCode = OpenAxisCodesByDimension(generated, taxonomies);
            var referenceOpenAxisCode = OpenAxisCodesByDimension(reference, taxonomies);
            var comparableOpenAxisCode = Comparable(generatedOpenAxisCode, referenceOpenAxisCode);
            yield return PlaneBSupport.LiteralWithDeclaredDivergences(
                "B-AXE-04", ValidationLayer.Critical, "mAxis", "AxisCode of open axes: literal against the reference",
                "B-AXE-04-OPEN-AXISCODE", referenceRole, comparableOpenAxisCode, exceptionSink, sw.ElapsedMilliseconds);
        }

        // ---- B-AXE-05: AxisLabel of closed axes (informative) ----
        sw.Restart();
        var generatedClosedLabel = ClosedAxisLabels(generated, taxonomies);
        var referenceClosedLabel = ClosedAxisLabels(reference, taxonomies);
        var comparableClosedLabel = Comparable(generatedClosedLabel, referenceClosedLabel);
        yield return PlaneBSupport.LiteralWithDeclaredDivergences(
            "B-AXE-05", ValidationLayer.Informative, "mAxis", "AxisLabel of closed axes (informative)",
            "B-AXE-05-CLOSED-AXISLABEL", referenceRole, comparableClosedLabel, exceptionSink, sw.ElapsedMilliseconds);

        // ---- B-LAB-01: OrdinateLabel of closed axes, whitespace-insensitive, informative ----
        sw.Restart();
        var comparableOrdinateLabel = ComparableOrdinateLabels(generated, reference, generatedPaths, referencePaths, tableIdsGenerated, tableIdsReference);
        yield return PlaneBSupport.LiteralWithDeclaredDivergences(
            "B-LAB-01", ValidationLayer.Informative, "mAxisOrdinate", "OrdinateLabel of closed axes, whitespace-insensitive",
            "B-LAB-01-ORDINATE-LABEL", referenceRole, comparableOrdinateLabel, exceptionSink, sw.ElapsedMilliseconds);

        // ---- B-CEL-01: coverage of cell positions ----
        sw.Restart();
        var generatedCellKeys = CellPositionKeysByBusinessTable(BusinessKeys.BuildClosedCellPositionIndex(generated, genTableIdSet), tableIdsGenerated);
        var referenceCellKeys = CellPositionKeysByBusinessTable(BusinessKeys.BuildClosedCellPositionIndex(reference, refTableIdSet), tableIdsReference);
        yield return PlaneBSupport.Containment(
            "B-CEL-01", ValidationLayer.Critical, "mTableCell", "Set of positions of each cell: generated contains the reference",
            "B-CEL-01-CELL-POSITIONS", referenceRole, generatedCellKeys, referenceCellKeys, exceptionSink, sw.ElapsedMilliseconds);

        // ---- B-CEL-02: IsShaded literal, with declared divergences ----
        sw.Restart();
        var comparableShaded = ComparableIsShaded(generated, reference, tableIdsGenerated, tableIdsReference, genTableIdSet, refTableIdSet);
        yield return PlaneBSupport.LiteralWithDeclaredDivergences(
            "B-CEL-02", ValidationLayer.Critical, "mTableCell", "IsShaded literal against the reference",
            "B-CEL-02", referenceRole, comparableShaded, exceptionSink, sw.ElapsedMilliseconds);

        // ---- B-OCA-01: effective closure ----
        sw.Restart();
        yield return EffectiveClosureMatchesReference(generated, reference, referenceRole, tableIdsGenerated, tableIdsReference, model, sw);

        // ---- B-OAR-01: presence and HierarchyID of open-axis restrictions ----
        sw.Restart();
        var generatedRestriction = OpenAxisRestrictionHierarchyByDimension(generated, taxonomies);
        var referenceRestriction = OpenAxisRestrictionHierarchyByDimension(reference, taxonomies);
        yield return PlaneBSupport.Containment(
            "B-OAR-01.1", ValidationLayer.Critical, "mOpenAxisValueRestriction", "Presence of open-axis restriction: generated contains the reference",
            "B-OAR-01-PRESENCE", referenceRole, generatedRestriction.Keys.ToHashSet(StringComparer.Ordinal), referenceRestriction.Keys.ToHashSet(StringComparer.Ordinal), exceptionSink, sw.ElapsedMilliseconds);

        sw.Restart();
        var comparableHierarchy = Comparable(
            generatedRestriction.ToDictionary(kv => kv.Key, kv => (string?)Normalization.HierarchyCode(kv.Value)),
            referenceRestriction.ToDictionary(kv => kv.Key, kv => (string?)Normalization.HierarchyCode(kv.Value)));
        yield return PlaneBSupport.LiteralWithDeclaredDivergences(
            "B-OAR-01.2", ValidationLayer.Critical, "mOpenAxisValueRestriction", "HierarchyID of the restriction (normalised): literal against the reference",
            "B-OAR-01-HIERARCHY", referenceRole, comparableHierarchy, exceptionSink, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// A physical <c>TableID</c> can be linked to SEVERAL taxonomies (N:M via
    /// <c>mTaxonomyTable</c>): the reverse of <paramref name="tableIds"/> is NOT a
    /// function, it is a 1:N relation. Use <c>ToLookup</c>, never <c>ToDictionary</c>.
    /// </summary>
    private static ILookup<int, string> BusinessKeysByTableId(Dictionary<BusinessKeys.TableKey, int> tableIds) =>
        tableIds.ToLookup(kv => kv.Value, kv => kv.Key.Taxonomy + "|" + kv.Key.TableCode);

    private static HashSet<string> PathKeysByBusinessTable(
        Dictionary<(int TableId, string Axis, string PathKey), int> index, Dictionary<BusinessKeys.TableKey, int> tableIds)
    {
        var tableKeysById = BusinessKeysByTableId(tableIds);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (tableId, axis, pathKey) in index.Keys)
        {
            foreach (var tableBusinessKey in tableKeysById[tableId])
            {
                result.Add(tableBusinessKey + "|" + axis + "|" + pathKey);
            }
        }

        return result;
    }

    private static HashSet<string> CellPositionKeysByBusinessTable(
        Dictionary<(int TableId, string PositionKey), int> index, Dictionary<BusinessKeys.TableKey, int> tableIds)
    {
        var tableKeysById = BusinessKeysByTableId(tableIds);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (tableId, positionKey) in index.Keys)
        {
            foreach (var tableBusinessKey in tableKeysById[tableId])
            {
                result.Add(tableBusinessKey + "|" + positionKey);
            }
        }

        return result;
    }

    private static HashSet<string> AxisSignatures(SqliteConnection c, IReadOnlySet<string> taxonomies)
    {
        var rows = SqlHelpers.Rows(
            c,
            """
            SELECT t."TaxonomyCode", tab."TableCode", a."AxisOrientation", a."IsOpenAxis", ta."Order"
            FROM "mTaxonomyTable" tt
            JOIN "mTaxonomy" t ON t."TaxonomyID" = tt."TaxonomyID"
            JOIN "mTable" tab ON tab."TableID" = tt."TableID"
            JOIN "mTableAxis" ta ON ta."TableID" = tab."TableID"
            JOIN "mAxis" a ON a."AxisID" = ta."AxisID"
            """,
            5);

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var taxonomy = Normalization.TaxonomyCode(row[0]!);
            if (taxonomies.Contains(taxonomy))
            {
                result.Add($"{taxonomy}|{row[1]}|{row[4]}|{row[2]}|{row[3]}");
            }
        }

        return result;
    }

    private static Dictionary<string, string?> OpenAxisCodesByDimension(SqliteConnection c, IReadOnlySet<string> taxonomies)
    {
        var rows = SqlHelpers.Rows(
            c,
            """
            SELECT t."TaxonomyCode", tab."TableCode", dim."DimensionXBRLCode", a."AxisCode"
            FROM "mAxis" a
            JOIN "mTableAxis" ta ON ta."AxisID" = a."AxisID"
            JOIN "mTable" tab ON tab."TableID" = ta."TableID"
            JOIN "mTaxonomyTable" tt ON tt."TableID" = tab."TableID"
            JOIN "mTaxonomy" t ON t."TaxonomyID" = tt."TaxonomyID"
            JOIN "mAxisOrdinate" o ON o."AxisID" = a."AxisID"
            JOIN "mOrdinateCategorisation" oc ON oc."OrdinateID" = o."OrdinateID" AND oc."MemberID" = 9999
            JOIN "mDimension" dim ON dim."DimensionID" = oc."DimensionID"
            WHERE a."IsOpenAxis" = 1
            """,
            4);

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var taxonomy = Normalization.TaxonomyCode(row[0]!);
            if (!taxonomies.Contains(taxonomy))
            {
                continue;
            }

            var key = taxonomy + "|" + row[1] + "|" + Normalization.XbrlLocalPart(row[2]!);
            result[key] = row[3];
        }

        return result;
    }

    private static Dictionary<string, string?> ClosedAxisLabels(SqliteConnection c, IReadOnlySet<string> taxonomies)
    {
        var rows = SqlHelpers.Rows(
            c,
            """
            SELECT t."TaxonomyCode", tab."TableCode", a."AxisOrientation", a."AxisLabel"
            FROM "mTaxonomyTable" tt
            JOIN "mTaxonomy" t ON t."TaxonomyID" = tt."TaxonomyID"
            JOIN "mTable" tab ON tab."TableID" = tt."TableID"
            JOIN "mTableAxis" ta ON ta."TableID" = tab."TableID"
            JOIN "mAxis" a ON a."AxisID" = ta."AxisID"
            WHERE a."IsOpenAxis" = 0
            """,
            4);

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var taxonomy = Normalization.TaxonomyCode(row[0]!);
            if (taxonomies.Contains(taxonomy))
            {
                result[taxonomy + "|" + row[1] + "|" + row[2]] = row[3];
            }
        }

        return result;
    }

    private static Dictionary<string, (string Generated, string Reference)> ComparableOrdinateLabels(
        SqliteConnection generated, SqliteConnection reference,
        Dictionary<(int TableId, string Axis, string PathKey), int> generatedPaths,
        Dictionary<(int TableId, string Axis, string PathKey), int> referencePaths,
        Dictionary<BusinessKeys.TableKey, int> tableIdsGenerated,
        Dictionary<BusinessKeys.TableKey, int> tableIdsReference)
    {
        var generatedLabels = SqlHelpers.Rows(generated, "SELECT \"OrdinateID\", \"OrdinateLabel\" FROM \"mAxisOrdinate\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]);
        var referenceLabels = SqlHelpers.Rows(reference, "SELECT \"OrdinateID\", \"OrdinateLabel\" FROM \"mAxisOrdinate\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]);

        var referenceByTaxTable = tableIdsReference.ToLookup(kv => kv.Value, kv => kv.Key);

        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var ((tableId, axis, path), refOrdinateId) in referencePaths)
        {
            foreach (var tableKey in referenceByTaxTable[tableId])
            {
                if (!tableIdsGenerated.TryGetValue(tableKey, out var genTableId)
                    || !generatedPaths.TryGetValue((genTableId, axis, path), out var genOrdinateId))
                {
                    continue; // absence is covered by B-AXE-02
                }

                var key = tableKey.Taxonomy + "|" + tableKey.TableCode + "|" + axis + "|" + path;
                var refLabel = Normalization.CollapseWhitespace(referenceLabels.GetValueOrDefault(refOrdinateId));
                var genLabel = Normalization.CollapseWhitespace(generatedLabels.GetValueOrDefault(genOrdinateId));
                result[key] = (genLabel, refLabel);
            }
        }

        return result;
    }

    private static Dictionary<string, (string Generated, string Reference)> ComparableIsShaded(
        SqliteConnection generated, SqliteConnection reference,
        Dictionary<BusinessKeys.TableKey, int> tableIdsGenerated, Dictionary<BusinessKeys.TableKey, int> tableIdsReference,
        IReadOnlySet<int> genTableIdSet, IReadOnlySet<int> refTableIdSet)
    {
        var generatedIndex = BusinessKeys.BuildClosedCellPositionIndex(generated, genTableIdSet);
        var referenceIndex = BusinessKeys.BuildClosedCellPositionIndex(reference, refTableIdSet);

        var generatedShaded = SqlHelpers.Rows(generated, "SELECT \"CellID\", \"IsShaded\" FROM \"mTableCell\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]!);
        var referenceShaded = SqlHelpers.Rows(reference, "SELECT \"CellID\", \"IsShaded\" FROM \"mTableCell\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]!);
        var referenceBusinessCode = SqlHelpers.Rows(reference, "SELECT \"CellID\", \"BusinessCode\" FROM \"mTableCell\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]!);

        // A reference TableID can correspond to SEVERAL business keys (a table reused across
        // taxonomies): it is enough that ONE of them resolves on the generated side, because the
        // physical cell (positions/shading) is the same regardless of which taxonomy references it.
        var refTableKeysById = tableIdsReference.ToLookup(kv => kv.Value, kv => kv.Key);
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var ((refTableId, positionKey), refCellId) in referenceIndex)
        {
            foreach (var refTableKey in refTableKeysById[refTableId])
            {
                if (!tableIdsGenerated.TryGetValue(refTableKey, out var genTableId)
                    || !generatedIndex.TryGetValue((genTableId, positionKey), out var genCellId))
                {
                    continue; // absence (no candidate resolves) is covered by B-CEL-01
                }

                // Key = BusinessCode of the REFERENCE (this is how the known exceptions are named).
                var key = referenceBusinessCode.GetValueOrDefault(refCellId, $"cell#{refCellId}");
                result[key] = (generatedShaded.GetValueOrDefault(genCellId, "?"), referenceShaded.GetValueOrDefault(refCellId, "?"));
                break;
            }
        }

        return result;
    }

    private static Dictionary<string, string> OpenAxisRestrictionHierarchyByDimension(SqliteConnection c, IReadOnlySet<string> taxonomies)
    {
        var rows = SqlHelpers.Rows(
            c,
            """
            SELECT t."TaxonomyCode", tab."TableCode", dim."DimensionXBRLCode", h."HierarchyCode"
            FROM "mOpenAxisValueRestriction" r
            JOIN "mAxis" a ON a."AxisID" = r."AxisID"
            JOIN "mTableAxis" ta ON ta."AxisID" = a."AxisID"
            JOIN "mTable" tab ON tab."TableID" = ta."TableID"
            JOIN "mTaxonomyTable" tt ON tt."TableID" = tab."TableID"
            JOIN "mTaxonomy" t ON t."TaxonomyID" = tt."TaxonomyID"
            JOIN "mHierarchy" h ON h."HierarchyID" = r."HierarchyID"
            JOIN "mAxisOrdinate" o ON o."AxisID" = a."AxisID"
            JOIN "mOrdinateCategorisation" oc ON oc."OrdinateID" = o."OrdinateID" AND oc."MemberID" = 9999
            JOIN "mDimension" dim ON dim."DimensionID" = oc."DimensionID"
            """,
            4);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var taxonomy = Normalization.TaxonomyCode(row[0]!);
            if (taxonomies.Contains(taxonomy))
            {
                result[taxonomy + "|" + row[1] + "|" + Normalization.XbrlLocalPart(row[2]!)] = row[3]!;
            }
        }

        return result;
    }

    private static Dictionary<string, (string Generated, string Reference)> Comparable(Dictionary<string, string?> generated, Dictionary<string, string?> reference)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var (key, refValue) in reference)
        {
            if (generated.TryGetValue(key, out var genValue))
            {
                result[key] = (genValue ?? "(NULL)", refValue ?? "(NULL)");
            }
        }

        return result;
    }

    private static CheckResult EffectiveClosureMatchesReference(
        SqliteConnection generated, SqliteConnection reference, string referenceRole,
        Dictionary<BusinessKeys.TableKey, int> tableIdsGenerated, Dictionary<BusinessKeys.TableKey, int> tableIdsReference,
        ValidationSourceModel model, System.Diagnostics.Stopwatch sw)
    {
        var generatedClosure = ClosureIndexBuilder.Build(generated, tableIdsGenerated.Values.ToHashSet());
        var referenceClosure = ClosureIndexBuilder.Build(reference, tableIdsReference.Values.ToHashSet());
        var generatedPathIndex = BusinessKeys.BuildClosedOrdinatePathIndex(generated, tableIdsGenerated.Values.ToHashSet());
        var referencePathIndex = BusinessKeys.BuildClosedOrdinatePathIndex(reference, tableIdsReference.Values.ToHashSet());

        var compared = 0;
        var mismatches = new List<CheckSample>();

        foreach (var (tableKey, refTableId) in tableIdsReference)
        {
            if (!tableIdsGenerated.TryGetValue(tableKey, out var genTableId))
            {
                continue; // covered by B-TPL-03
            }

            foreach (var ((tableId, axis, path), refOrdinateId) in referencePathIndex)
            {
                if (tableId != refTableId || !referenceClosure.LeafOrdinateIds.Contains(refOrdinateId))
                {
                    continue;
                }

                if (!generatedPathIndex.TryGetValue((genTableId, axis, path), out var genOrdinateId))
                {
                    continue; // covered by B-AXE-02
                }

                compared++;
                // When reading the REFERENCE, the metric IS inherited (4.0/4.2 inherit it); when
                // reading the GENERATED output, the closure is already in its own rows.
                var refSet = referenceClosure.WalkedClosureAllowingMetricInheritance(refOrdinateId);
                var genSet = generatedClosure.OwnRowsAsClosure(genOrdinateId);

                // B-OCA-01 originally encoded an EQUALITY that is actually a property of DPM 1.0,
                // not of the format. The real criterion of this layer is COVERAGE: the generated
                // output contains the reference, never less. We emit the full closure while the
                // reference stores the MINIMISED form (and, for DPM 2.0, also the derived resets),
                // so equality was never the right invariant; it only held by chance while the sole
                // source was DPM 1.0.
                //
                // Measured before unifying: over the complete DPM 1.0 output against its primary
                // content reference (3.2), equality still holds at 6,877/6,877 = 100.0000%. In
                // DPM 1.0 equality IS therefore a real property of that output, so it is KEPT there.
                // The check is scoped by ValidationSourceModel: the DPM 1.0 form does not change.
                var violates = model == ValidationSourceModel.Dpm2
                    ? !genSet.IsSupersetOf(refSet)
                    : !refSet.SetEquals(genSet);

                if (violates)
                {
                    // What matters for the next diagnosis is not "there is a difference" but WHICH
                    // pairs the reference has that the generated output lacks: the real gap,
                    // whether the criterion is equality (DPM 1.0) or containment (DPM 2.0).
                    var missing = refSet.Except(genSet).Select(FormatPair).ToList();
                    mismatches.Add(new CheckSample(
                        $"{tableKey.Taxonomy}/{tableKey.TableCode}/{axis}/{path}",
                        Reference: missing.Count == 0 ? null : string.Join(";", missing)));
                }
            }
        }

        var statement = model == ValidationSourceModel.Dpm2
            ? "mOrdinateCategorisation: generated CONTAINS the effective closure of the reference (coverage, not equality)"
            : "mOrdinateCategorisation by EFFECTIVE CLOSURE (not row by row)";

        return CheckResult.FromViolationCount(
            "B-OCA-01", ValidationPlane.B, ValidationLayer.Critical, "mOrdinateCategorisation",
            statement, compared, mismatches.Count, sw.ElapsedMilliseconds, mismatches);
    }

    private static string FormatPair((string Dimension, string? Member) pair) => pair.Dimension + "=" + (pair.Member ?? "(NULL)");
}
