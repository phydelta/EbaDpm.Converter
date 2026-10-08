using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.Checks;

/// <summary>
/// Cells and positions, ordinate categorisation and open-axis restrictions.
/// Plane A, without looking at any reference.
/// </summary>
public static class CellChecks
{
    public static IEnumerable<CheckResult> Run(SqliteConnection c, ValidationSourceModel model)
    {
        yield return BusinessCodeReconstructedMatchesEmitted(c, model);

        // A-CEL-01/A-CEL-02 encoded a property of DPM 1.0 (the BusinessCode only counts CLOSED
        // axes), not of the format: the 4.2 reference itself violates them 45,290 times out of
        // 163,206. In DPM 2.0 an OPEN Z (or Y) axis contributes a LITERAL segment to the
        // BusinessCode (ComposeBusinessCode already includes it, Dpm20AxisAndCellLoader), so on that
        // branch the segment count covers ALL positions, not only closed-axis ones.
        // Restated per scope: the DPM 1.0 form does not change.
        var cel02PositionFilter = model == ValidationSourceModel.Dpm2 ? "" : "AND a.\"IsOpenAxis\"=0";
        yield return SqlHelpers.CountCheck(
            c, "A-CEL-02", ValidationLayer.Critical, "mTableCell",
            model == ValidationSourceModel.Dpm2
                ? "Number of BusinessCode segments == 1 + TOTAL number of positions (an open axis also contributes a segment)"
                : "Number of BusinessCode segments == 1 + number of positions on closed axes",
            "SELECT COUNT(*) FROM \"mTableCell\"",
            $"""
            SELECT COUNT(*) FROM "mTableCell" c
            WHERE (SELECT COUNT(*) FROM "mCellPosition" cp JOIN "mAxisOrdinate" o ON o."OrdinateID"=cp."OrdinateID" JOIN "mAxis" a ON a."AxisID"=o."AxisID" WHERE cp."CellID"=c."CellID" {cel02PositionFilter})
                <> (length(c."BusinessCode") - length(replace(c."BusinessCode", ',', '')))
            """);

        yield return SqlHelpers.CountCheck(
            c, "A-CEL-03.1", ValidationLayer.Critical, "mTableCell", "BusinessCode never NULL or empty",
            "SELECT COUNT(*) FROM \"mTableCell\"", "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"BusinessCode\" IS NULL OR TRIM(\"BusinessCode\") = ''");
        yield return SqlHelpers.CountCheck(
            c, "A-CEL-03.2", ValidationLayer.Critical, "mTableCell", "BusinessCode always has the form '{...}' whose first segment is the TableCode of its table",
            "SELECT COUNT(*) FROM \"mTableCell\"",
            """
            SELECT COUNT(*) FROM "mTableCell" c JOIN "mTable" t ON t."TableID" = c."TableID"
            WHERE c."BusinessCode" IS NULL
               OR c."BusinessCode" NOT LIKE '{' || t."TableCode" || ',%' AND c."BusinessCode" <> '{' || t."TableCode" || '}'
            """);

        yield return SqlHelpers.CountCheck(
            c, "A-CEL-04.1", ValidationLayer.Critical, "mTableCell", "Every cell has >=1 X position and >=1 Y position",
            "SELECT COUNT(*) FROM \"mTableCell\"",
            """
            SELECT COUNT(*) FROM "mTableCell" c
            WHERE NOT EXISTS (SELECT 1 FROM "mCellPosition" cp JOIN "mAxisOrdinate" o ON o."OrdinateID"=cp."OrdinateID" JOIN "mAxis" a ON a."AxisID"=o."AxisID" WHERE cp."CellID"=c."CellID" AND a."AxisOrientation"='X')
               OR NOT EXISTS (SELECT 1 FROM "mCellPosition" cp JOIN "mAxisOrdinate" o ON o."OrdinateID"=cp."OrdinateID" JOIN "mAxis" a ON a."AxisID"=o."AxisID" WHERE cp."CellID"=c."CellID" AND a."AxisOrientation"='Y')
            """);
        yield return SqlHelpers.CountCheck(
            c, "A-CEL-04.2", ValidationLayer.Critical, "mCellPosition", "Exactly one position per axis it takes part in",
            "SELECT COUNT(DISTINCT \"CellID\") FROM \"mCellPosition\"",
            """
            SELECT COUNT(*) FROM (
                SELECT cp."CellID", a."AxisID", COUNT(*) AS n FROM "mCellPosition" cp
                JOIN "mAxisOrdinate" o ON o."OrdinateID" = cp."OrdinateID"
                JOIN "mAxis" a ON a."AxisID" = o."AxisID"
                GROUP BY cp."CellID", a."AxisID" HAVING n > 1)
            """);

        yield return SqlHelpers.CountCheck(
            c, "A-CEL-05", ValidationLayer.Critical, "mTableCell", "No cell without positions, none with fewer than 2",
            "SELECT COUNT(*) FROM \"mTableCell\"",
            """
            SELECT COUNT(*) FROM "mTableCell" c
            WHERE (SELECT COUNT(*) FROM "mCellPosition" cp WHERE cp."CellID" = c."CellID") < 2
            """);

        yield return SqlHelpers.CountCheck(
            c, "A-CEL-06", ValidationLayer.Critical, "mCellPosition", "The ordinate of every position belongs to an axis of the same table as the cell",
            "SELECT COUNT(*) FROM \"mCellPosition\"",
            """
            SELECT COUNT(*) FROM "mCellPosition" cp
            JOIN "mTableCell" c ON c."CellID" = cp."CellID"
            JOIN "mAxisOrdinate" o ON o."OrdinateID" = cp."OrdinateID"
            JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE NOT EXISTS (SELECT 1 FROM "mTableAxis" ta WHERE ta."AxisID" = a."AxisID" AND ta."TableID" = c."TableID")
            """);

        yield return AbstractHeaderImpliesShaded(c);

        yield return SqlHelpers.CountCheck(
            c, "A-CEL-08", ValidationLayer.Critical, "mTableCell", "IsRowKey is constant and equals 0",
            "SELECT COUNT(*) FROM \"mTableCell\"", "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"IsRowKey\" IS NOT 0");

        yield return SqlHelpers.CountCheck(
            c, "A-CEL-09", ValidationLayer.Critical, "mCellPosition", "No position points to an X-axis ordinate with IsRowKey=1",
            "SELECT COUNT(*) FROM \"mCellPosition\"",
            "SELECT COUNT(*) FROM \"mCellPosition\" cp JOIN \"mAxisOrdinate\" o ON o.\"OrdinateID\"=cp.\"OrdinateID\" JOIN \"mAxis\" a ON a.\"AxisID\"=o.\"AxisID\" WHERE o.\"IsRowKey\"=1 AND a.\"AxisOrientation\"='X'");

        // ---- A-OCA-01/01b: effective closure == own set ----
        yield return EffectiveClosureEqualsOwnRows(c);
        yield return CheckResult.Skip(
            "A-OCA-01b", ValidationPlane.A, ValidationLayer.Critical, "(conversion)",
            "SkippedDefaultMemberResets == 0 (corollary of A-OCA-01)",
            "The counter lives in the CONVERSION report (stdout/--report of RunConvert), not in the database: --validate only opens the SQLite file. A-OCA-01 checks here the equivalent structural guarantee (effective closure == own set) without needing that counter.");

        yield return SqlHelpers.CountCheck(
            c, "A-OCA-02", ValidationLayer.Critical, "mOrdinateCategorisation", "Source NULL in 100% of rows",
            "SELECT COUNT(*) FROM \"mOrdinateCategorisation\"", "SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"Source\" IS NOT NULL");

        yield return SqlHelpers.CountCheck(
            c, "A-OCA-03", ValidationLayer.Critical, "mOrdinateCategorisation", "Every member of a categorisation belongs to the domain of its dimension (except the sentinel 9999)",
            "SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"MemberID\" <> 9999",
            """
            SELECT COUNT(*) FROM "mOrdinateCategorisation" oc
            JOIN "mDimension" dim ON dim."DimensionID" = oc."DimensionID"
            JOIN "mMember" m ON m."MemberID" = oc."MemberID"
            WHERE oc."MemberID" <> 9999 AND m."DomainID" <> dim."DomainID"
            """);

        yield return SqlHelpers.CountCheck(
            c, "A-OCA-04", ValidationLayer.Informative, "mAxisOrdinate", "Ordinates with cells and without categorisation (empty categorisation in the source Access)",
            "SELECT COUNT(DISTINCT cp.\"OrdinateID\") FROM \"mCellPosition\" cp",
            """
            SELECT COUNT(DISTINCT cp."OrdinateID") FROM "mCellPosition" cp
            JOIN "mAxisOrdinate" o ON o."OrdinateID" = cp."OrdinateID"
            JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 0
              AND NOT EXISTS (SELECT 1 FROM "mOrdinateCategorisation" oc WHERE oc."OrdinateID" = o."OrdinateID")
            """);

        yield return OpenAxisRestrictionIffUntypedDomain(c);

        yield return SqlHelpers.CountCheck(
            c, "A-OCA-06.1", ValidationLayer.Critical, "mOpenAxisValueRestriction", "At most one row per AxisID",
            "SELECT COUNT(*) FROM \"mOpenAxisValueRestriction\"",
            "SELECT COUNT(*) FROM (SELECT \"AxisID\" FROM \"mOpenAxisValueRestriction\" GROUP BY \"AxisID\" HAVING COUNT(*) > 1)");
        yield return SqlHelpers.CountCheck(
            c, "A-OCA-06.2", ValidationLayer.Critical, "mOpenAxisValueRestriction", "No restriction on a closed axis",
            "SELECT COUNT(*) FROM \"mOpenAxisValueRestriction\"",
            "SELECT COUNT(*) FROM \"mOpenAxisValueRestriction\" r JOIN \"mAxis\" a ON a.\"AxisID\"=r.\"AxisID\" WHERE a.\"IsOpenAxis\"=0");

        // A-OCA-07 requires "NULL <=> NULL" and is an invariant of DPM 1.0: the 4.2 reference
        // violates it 207 of 207 times and the 4.0 one 45 of 45; only the 3.2 satisfies it. In
        // DPM 2.0 the statement becomes A-OAR-02: HierarchyStartingMemberID ALWAYS NULL and
        // IsStartingMemberIncluded ALWAYS 0 (false, never null). The source does not model the
        // starting member, so 0 does not mean "no data", it means "no starting member".
        if (model == ValidationSourceModel.Dpm2)
        {
            yield return SqlHelpers.CountCheck(
                c, "A-OAR-02", ValidationLayer.Critical, "mOpenAxisValueRestriction",
                "HierarchyStartingMemberID NULL and IsStartingMemberIncluded=0 (false, never null)",
                "SELECT COUNT(*) FROM \"mOpenAxisValueRestriction\"",
                "SELECT COUNT(*) FROM \"mOpenAxisValueRestriction\" WHERE \"HierarchyStartingMemberID\" IS NOT NULL OR \"IsStartingMemberIncluded\" IS NOT 0");
        }
        else
        {
            yield return SqlHelpers.CountCheck(
                c, "A-OCA-07", ValidationLayer.Critical, "mOpenAxisValueRestriction", "IsStartingMemberIncluded NULL <=> HierarchyStartingMemberID NULL",
                "SELECT COUNT(*) FROM \"mOpenAxisValueRestriction\"",
                "SELECT COUNT(*) FROM \"mOpenAxisValueRestriction\" WHERE (\"HierarchyStartingMemberID\" IS NULL) IS NOT (\"IsStartingMemberIncluded\" IS NULL)");
        }

        yield return SqlHelpers.CountCheck(
            c, "A-OCA-08", ValidationLayer.Critical, "mOpenAxisValueRestriction", "The starting member, when present, belongs to the referenced hierarchy",
            "SELECT COUNT(*) FROM \"mOpenAxisValueRestriction\" WHERE \"HierarchyStartingMemberID\" IS NOT NULL",
            """
            SELECT COUNT(*) FROM "mOpenAxisValueRestriction" r
            WHERE r."HierarchyStartingMemberID" IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM "mHierarchyNode" n WHERE n."HierarchyID" = r."HierarchyID" AND n."MemberID" = r."HierarchyStartingMemberID")
            """);

        yield return SqlHelpers.CountCheck(
            c, "A-OCA-09", ValidationLayer.Critical, "mOpenAxisValueRestriction", "No restriction on a hierarchy of a TYPED domain",
            "SELECT COUNT(*) FROM \"mOpenAxisValueRestriction\"",
            """
            SELECT COUNT(*) FROM "mOpenAxisValueRestriction" r
            JOIN "mHierarchy" h ON h."HierarchyID" = r."HierarchyID"
            JOIN "mDomain" d ON d."DomainID" = h."DomainID"
            WHERE d."IsTypedDomain" = 1
            """);

        // ONE DIMENSION, ONE AXIS: two plane A invariants that need no reference (the statement
        // goes in the property, never in the figure). IDs "A-OCA-10"/"A-CEL-10" are used because
        // "A-OCA-02"/"A-CEL-06" already exist with a different statement.
        //
        // Scoped to ValidationSourceModel.Dpm2: measured over the complete DPM 1.0 output (4.1)
        // there are 0 violations of both, with the DPM 1.0 pipeline untouched. They are left ONLY
        // in Dpm2 anyway: enabling them for DPM 1.0 is a scope decision.
        if (model == ValidationSourceModel.Dpm2)
        {
            yield return SqlHelpers.CountCheck(
                c, "A-OCA-10", ValidationLayer.Critical, "mOrdinateCategorisation",
                "A dimension belongs to exactly ONE axis within a given table: no (TableID,DimensionID) touches ordinates of more than one axis",
                """
                SELECT COUNT(*) FROM (
                    SELECT ta."TableID", oc."DimensionID"
                    FROM "mOrdinateCategorisation" oc
                    JOIN "mAxisOrdinate" ao ON ao."OrdinateID" = oc."OrdinateID"
                    JOIN "mAxis" a ON a."AxisID" = ao."AxisID"
                    JOIN "mTableAxis" ta ON ta."AxisID" = a."AxisID"
                    GROUP BY ta."TableID", oc."DimensionID"
                )
                """,
                """
                SELECT COUNT(*) FROM (
                    SELECT ta."TableID", oc."DimensionID"
                    FROM "mOrdinateCategorisation" oc
                    JOIN "mAxisOrdinate" ao ON ao."OrdinateID" = oc."OrdinateID"
                    JOIN "mAxis" a ON a."AxisID" = ao."AxisID"
                    JOIN "mTableAxis" ta ON ta."AxisID" = a."AxisID"
                    GROUP BY ta."TableID", oc."DimensionID"
                    HAVING COUNT(DISTINCT a."AxisOrientation") > 1
                )
                """);

            yield return SqlHelpers.CountCheck(
                c, "A-CEL-10", ValidationLayer.Critical, "mCellPosition",
                "One cell, one member per dimension: no NON-shaded cell receives two different members of the same dimension from its own ordinates (recomposed via mCellPosition)",
                "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"IsShaded\" = 0",
                """
                SELECT COUNT(DISTINCT "CellID") FROM (
                    SELECT cp."CellID", oc."DimensionID"
                    FROM "mCellPosition" cp
                    JOIN "mOrdinateCategorisation" oc ON oc."OrdinateID" = cp."OrdinateID"
                    JOIN "mTableCell" tc ON tc."CellID" = cp."CellID"
                    WHERE tc."IsShaded" = 0
                    GROUP BY cp."CellID", oc."DimensionID"
                    HAVING COUNT(DISTINCT oc."MemberID") > 1
                )
                """);
        }
    }

    private static CheckResult BusinessCodeReconstructedMatchesEmitted(SqliteConnection c, ValidationSourceModel model)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cells = SqlHelpers.Rows(
            c, "SELECT c.\"CellID\", c.\"BusinessCode\", tab.\"TableCode\" FROM \"mTableCell\" c JOIN \"mTable\" tab ON tab.\"TableID\" = c.\"TableID\"", 3);

        // In DPM 2.0 ComposeBusinessCode (Dpm20AxisAndCellLoader) ALSO includes the OPEN axes, and
        // so does the 4.2 reference (the DPM 1.0 form yields 45,290/163,206 "violations" on both).
        // Reconstruction faithful to the production algorithm: PRIMARY order by orientation
        // (Y,X,Z), SECONDARY by AxisID DESCENDING within the same orientation (several open axes
        // of the same orientation). DPM 1.0 keeps the original form: closed axes only, with no
        // AxisID tie-break (never needed, 0 cases).
        var positionsSql = model == ValidationSourceModel.Dpm2
            ? """
              SELECT cp."CellID", a."AxisOrientation", o."OrdinateCode", a."AxisID" FROM "mCellPosition" cp
              JOIN "mAxisOrdinate" o ON o."OrdinateID" = cp."OrdinateID"
              JOIN "mAxis" a ON a."AxisID" = o."AxisID"
              """
            : """
              SELECT cp."CellID", a."AxisOrientation", o."OrdinateCode", a."AxisID" FROM "mCellPosition" cp
              JOIN "mAxisOrdinate" o ON o."OrdinateID" = cp."OrdinateID"
              JOIN "mAxis" a ON a."AxisID" = o."AxisID"
              WHERE a."IsOpenAxis" = 0
              """;
        var positions = SqlHelpers.Rows(c, positionsSql, 4);

        var positionsByCell = positions
            .GroupBy(r => int.Parse(r[0]!))
            .ToDictionary(g => g.Key, g => g.Select(r => (Orientation: r[1]!, Code: r[2]!, AxisId: int.Parse(r[3]!))).ToList());

        var orientationOrder = new Dictionary<string, int> { ["Y"] = 0, ["X"] = 1, ["Z"] = 2 };
        var failures = new List<CheckSample>();

        foreach (var row in cells)
        {
            var cellId = int.Parse(row[0]!);
            var businessCode = row[1]!;
            var tableCode = row[2]!;

            var coords = positionsByCell.TryGetValue(cellId, out var list)
                ? list.OrderBy(p => orientationOrder[p.Orientation]).ThenByDescending(p => p.AxisId).Select(p => p.Code).ToList()
                : [];

            var reconstructed = "{" + tableCode + "," + string.Join(",", coords) + "}";
            if (!string.Equals(reconstructed, businessCode, StringComparison.Ordinal))
            {
                failures.Add(new CheckSample($"CellID={cellId}", reconstructed, businessCode));
            }
        }
        sw.Stop();

        return CheckResult.FromViolationCount(
            "A-CEL-01", ValidationPlane.A, ValidationLayer.Critical, "mTableCell",
            "BusinessCode rebuilt from mCellPosition == emitted BusinessCode, byte for byte",
            cells.Count, failures.Count, sw.ElapsedMilliseconds, failures);
    }

    private static CheckResult AbstractHeaderImpliesShaded(SqliteConnection c)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var violating = SqlHelpers.Scalar(
            c,
            """
            SELECT COUNT(DISTINCT c."CellID") FROM "mTableCell" c
            JOIN "mCellPosition" cp ON cp."CellID" = c."CellID"
            JOIN "mAxisOrdinate" o ON o."OrdinateID" = cp."OrdinateID"
            WHERE o."IsAbstractHeader" = 1 AND c."IsShaded" = 0
            """);
        var touching = SqlHelpers.Scalar(
            c,
            """
            SELECT COUNT(DISTINCT c."CellID") FROM "mTableCell" c
            JOIN "mCellPosition" cp ON cp."CellID" = c."CellID"
            JOIN "mAxisOrdinate" o ON o."OrdinateID" = cp."OrdinateID"
            WHERE o."IsAbstractHeader" = 1
            """);
        sw.Stop();
        return CheckResult.FromViolationCount(
            "A-CEL-07", ValidationPlane.A, ValidationLayer.Critical, "mTableCell",
            "IsAbstractHeader=1 => every cell touching that ordinate has IsShaded=1", touching, violating, sw.ElapsedMilliseconds, []);
    }

    /// <summary>
    /// A-OCA-01, over the GENERATED output only (plane A): the closure computed by walking the
    /// tree (full algorithm) equals the closure taken directly from the ordinate's own rows (the
    /// "safe form").
    /// </summary>
    private static CheckResult EffectiveClosureEqualsOwnRows(SqliteConnection c)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var allTableIds = SqlHelpers.Rows(c, "SELECT \"TableID\" FROM \"mTable\"", 1).Select(r => int.Parse(r[0]!)).ToHashSet();
        var closure = ClosureIndexBuilder.Build(c, allTableIds);

        var failures = new List<CheckSample>();
        foreach (var ordinateId in closure.LeafOrdinateIds)
        {
            var walked = closure.WalkedClosureExcludingMetricInheritance(ordinateId);
            var own = closure.OwnRowsAsClosure(ordinateId);
            if (!walked.SetEquals(own))
            {
                failures.Add(new CheckSample($"OrdinateID={ordinateId}"));
            }
        }
        sw.Stop();

        return CheckResult.FromViolationCount(
            "A-OCA-01", ValidationPlane.A, ValidationLayer.Critical, "mOrdinateCategorisation",
            "Effective closure == own set, for every non-abstract closed ordinate (100.0000% expected)",
            closure.LeafOrdinateIds.Count, failures.Count, sw.ElapsedMilliseconds, failures);
    }

    private static CheckResult OpenAxisRestrictionIffUntypedDomain(SqliteConnection c)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var violating = SqlHelpers.Scalar(
            c,
            """
            SELECT COUNT(*) FROM (
                SELECT a."AxisID", dom."IsTypedDomain" AS typed,
                       EXISTS(SELECT 1 FROM "mOpenAxisValueRestriction" r WHERE r."AxisID" = a."AxisID") AS has_restriction
                FROM "mAxis" a
                JOIN "mAxisOrdinate" o ON o."AxisID" = a."AxisID"
                JOIN "mOrdinateCategorisation" oc ON oc."OrdinateID" = o."OrdinateID" AND oc."MemberID" = 9999
                JOIN "mDimension" dim ON dim."DimensionID" = oc."DimensionID"
                JOIN "mDomain" dom ON dom."DomainID" = dim."DomainID"
                WHERE a."IsOpenAxis" = 1
            ) WHERE typed = has_restriction
            """);
        var total = SqlHelpers.Scalar(c, "SELECT COUNT(*) FROM \"mAxis\" WHERE \"IsOpenAxis\" = 1");
        sw.Stop();
        return CheckResult.FromViolationCount(
            "A-OCA-05", ValidationPlane.A, ValidationLayer.Critical, "mOpenAxisValueRestriction",
            "Open-axis restriction <=> NON-typed domain", total, violating, sw.ElapsedMilliseconds, []);
    }
}
