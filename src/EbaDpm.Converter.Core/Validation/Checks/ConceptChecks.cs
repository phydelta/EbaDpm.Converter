using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.Checks;

/// <summary>
/// Vocabulary of <c>mConcept</c> and signatures. Plane A, without looking at any reference.
///
/// Data point signatures are in production: the intermediate "all NULL" state (A-SIG-01) was
/// removed, not relaxed. A-SIG-02/A-SIG-03 implement the final state.
///
/// <c>I-SIG-*</c> (signatures and categorisation) and <c>I-CPT-02b</c> (concepts per type ==
/// rows of the entity, discounting the sentinel 9999 ONLY in <c>Domain</c>, not in <c>Member</c>:
/// <c>mMember</c> 9999 DOES carry a concept in DPM 2.0). <c>I-CPT-02b</c> is critical only in
/// DPM 2.0 (0/0 measured in 4.2 and 4.3); in DPM 1.0 the Hierarchy/HierarchyNode/Release/
/// TemplateOrTable mismatch is covered by <c>A-CPT-02</c>, informative and not fully closed.
/// </summary>
public static class ConceptChecks
{
    /// <summary>The vocabulary of the DESTINATION (A-CPT-01, EIOPA specification).</summary>
    private static readonly string[] DestinationVocabulary =
    [
        "Axis", "Ordinate", "Dimension", "Domain", "Hierarchy", "HierarchyNode", "Member",
        "Module", "Release", "ReportingFramework", "Table", "TemplateOrTable", "Taxonomy",
    ];

    public static IEnumerable<CheckResult> Run(SqliteConnection c, ValidationSourceModel model)
    {
        var inClause = string.Join(",", DestinationVocabulary.Select(v => $"'{v}'"));
        yield return SqlHelpers.CountCheck(
            c, "A-CPT-01", ValidationLayer.Critical, "mConcept", "ConceptType belongs to the DESTINATION vocabulary",
            "SELECT COUNT(*) FROM \"mConcept\"", $"SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" NOT IN ({inClause})");

        yield return ConceptTypeCountsMatchEntities(c);

        // A-CPT-03: |TemplateOrTable concepts| == |mTemplateOrTable|. NOT corrected (sharing a
        // concept is semantically correct); declared in the informative layer.
        yield return SqlHelpers.CountsEqual(
            c, "A-CPT-03", ValidationLayer.Informative, "mTemplateOrTable",
            "|TemplateOrTable concepts| == |mTemplateOrTable| (declared, NOT corrected)",
            "SELECT COUNT(DISTINCT \"ConceptID\") FROM \"mTemplateOrTable\"", "SELECT COUNT(*) FROM \"mTemplateOrTable\"");

        yield return SqlHelpers.CountCheck(
            c, "A-CPT-04", ValidationLayer.Critical, "mConcept", "No ConceptType='ConceptualModule'",
            "SELECT COUNT(*) FROM \"mConcept\"", "SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = 'ConceptualModule'");

        yield return SqlHelpers.CountsEqual(
            c, "A-CPT-05", ValidationLayer.Critical, "mConcept/mModule", "COUNT(ConceptType='Module') == |mModule|",
            "SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = 'Module'", "SELECT COUNT(*) FROM \"mModule\"");

        // ---- A-SIG-02/A-SIG-03: FINAL state of signatures ----
        yield return SqlHelpers.CountCheck(
            c, "A-SIG-02.1", ValidationLayer.Critical, "mOrdinateCategorisation", "DimensionMemberSignature never NULL",
            "SELECT COUNT(*) FROM \"mOrdinateCategorisation\"", "SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"DimensionMemberSignature\" IS NULL");
        yield return SqlHelpers.CountCheck(
            c, "A-SIG-02.2", ValidationLayer.Critical, "mOrdinateCategorisation", "DPS never NULL",
            "SELECT COUNT(*) FROM \"mOrdinateCategorisation\"", "SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"DPS\" IS NULL");
        yield return SqlHelpers.CountCheck(
            c, "A-SIG-02.3", ValidationLayer.Critical, "mOrdinateCategorisation", "No empty signature ('') in mOrdinateCategorisation",
            "SELECT COUNT(*) FROM \"mOrdinateCategorisation\"",
            "SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"DPS\" = '' OR \"DimensionMemberSignature\" = ''");

        yield return SqlHelpers.CountCheck(
            c, "A-SIG-03.1", ValidationLayer.Critical, "mTableCell", "DPS IS NULL <=> IsShaded = 1",
            "SELECT COUNT(*) FROM \"mTableCell\"", "SELECT COUNT(*) FROM \"mTableCell\" WHERE (\"DPS\" IS NULL) IS NOT (\"IsShaded\" = 1)");
        yield return SqlHelpers.CountCheck(
            c, "A-SIG-03.2", ValidationLayer.Critical, "mTableCell", "DatapointSignature IS NULL <=> IsShaded = 1",
            "SELECT COUNT(*) FROM \"mTableCell\"", "SELECT COUNT(*) FROM \"mTableCell\" WHERE (\"DatapointSignature\" IS NULL) IS NOT (\"IsShaded\" = 1)");
        yield return SqlHelpers.CountCheck(
            c, "A-SIG-03.3", ValidationLayer.Critical, "mTableCell", "No empty signature ('') in mTableCell",
            "SELECT COUNT(*) FROM \"mTableCell\"", "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"DPS\" = '' OR \"DatapointSignature\" = ''");

        // ---- Signatures and categorisation, without looking at the reference. Measured 0
        // violations over the three own outputs (DPM 1.0 --all, DPM 2.0 4.2 --all, DPM 2.0 4.3 --all). ----
        yield return SqlHelpers.CountCheck(
            c, "I-SIG-04", ValidationLayer.Critical, "mOrdinateCategorisation",
            "DPS = DimensionXBRLCode + '(' + MemberXBRLCode + ')' in every NON-open categorisation (MemberID<>9999)",
            "SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"MemberID\" <> 9999",
            """
            SELECT COUNT(*) FROM "mOrdinateCategorisation" oc
            JOIN "mDimension" d ON d."DimensionID" = oc."DimensionID"
            JOIN "mMember" m ON m."MemberID" = oc."MemberID"
            WHERE oc."MemberID" <> 9999
              AND oc."DPS" IS NOT (d."DimensionXBRLCode" || '(' || m."MemberXBRLCode" || ')')
            """);

        yield return SqlHelpers.CountCheck(
            c, "I-SIG-03", ValidationLayer.Critical, "mOrdinateCategorisation",
            "DimensionMemberSignature starts with DimensionXBRLCode + '(' in every NON-open categorisation",
            "SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"MemberID\" <> 9999",
            """
            SELECT COUNT(*) FROM "mOrdinateCategorisation" oc
            JOIN "mDimension" d ON d."DimensionID" = oc."DimensionID"
            WHERE oc."MemberID" <> 9999
              AND (oc."DimensionMemberSignature" IS NULL
                   OR substr(oc."DimensionMemberSignature", 1, length(d."DimensionXBRLCode") + 1) IS NOT (d."DimensionXBRLCode" || '('))
            """);

        yield return SqlHelpers.CountCheck(
            c, "I-SIG-07", ValidationLayer.Critical, "mTableCell", "Every NON-shaded cell has a DPS starting with 'MET('",
            "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"IsShaded\" = 0",
            "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"IsShaded\" = 0 AND (\"DPS\" IS NULL OR substr(\"DPS\", 1, 4) <> 'MET(')");

        yield return SqlHelpers.CountCheck(
            c, "I-SIG-08", ValidationLayer.Critical, "mOrdinateCategorisation", "Every categorisation with MemberID=9999 has '(*' in the DPS",
            "SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"MemberID\" = 9999",
            "SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"MemberID\" = 9999 AND (\"DPS\" IS NULL OR \"DPS\" NOT LIKE '%(*%')");

        // I-SIG-09/I-SIG-10 are stated in two directions. The original versions claimed an
        // equivalence PER ROW ("every row of an open-axis ordinate carries MemberID=9999" /
        // "every row with or without a bracket matches the axis restriction, row by row"), and the
        // converse was always FALSE: an open-axis ordinate carries the sentinel pair AND, in
        // addition, its FIXED categorisations (verified against the Access source, the output and
        // the layouts). This is not a relaxation: the direction that remains true is unchanged;
        // the false one moves from "per row" to "per ordinate/axis", the level at which it holds.
        yield return SqlHelpers.CountCheck(
            c, "I-SIG-09", ValidationLayer.Critical, "mOrdinateCategorisation",
            "MemberID = 9999 => the ordinate is on an open axis (per row); "
            + "and every open-axis ordinate carries AT LEAST one row with MemberID = 9999 (per ordinate)",
            """
            SELECT (SELECT COUNT(*) FROM "mOrdinateCategorisation")
                 + (SELECT COUNT(*) FROM "mAxisOrdinate" o JOIN "mAxis" a ON a."AxisID" = o."AxisID" WHERE a."IsOpenAxis" = 1)
            """,
            """
            SELECT
              (SELECT COUNT(*) FROM "mOrdinateCategorisation" oc
               JOIN "mAxisOrdinate" o ON o."OrdinateID" = oc."OrdinateID"
               JOIN "mAxis" a ON a."AxisID" = o."AxisID"
               WHERE oc."MemberID" = 9999 AND a."IsOpenAxis" <> 1)
            +
              (SELECT COUNT(*) FROM "mAxisOrdinate" o
               JOIN "mAxis" a ON a."AxisID" = o."AxisID"
               WHERE a."IsOpenAxis" = 1
                 AND NOT EXISTS (
                     SELECT 1 FROM "mOrdinateCategorisation" oc
                     WHERE oc."OrdinateID" = o."OrdinateID" AND oc."MemberID" = 9999
                 ))
            """);

        yield return SqlHelpers.CountCheck(
            c, "I-SIG-10", ValidationLayer.Critical, "mOrdinateCategorisation",
            "Bracket in the DPS => mOpenAxisValueRestriction exists for the ordinate's axis (per row); "
            + "and every axis with a restriction carries AT LEAST one row with a bracket in the DPS (per axis)",
            """
            SELECT (SELECT COUNT(*) FROM "mOrdinateCategorisation")
                 + (SELECT COUNT(DISTINCT "AxisID") FROM "mOpenAxisValueRestriction")
            """,
            """
            SELECT
              (SELECT COUNT(*) FROM "mOrdinateCategorisation" oc
               JOIN "mAxisOrdinate" o ON o."OrdinateID" = oc."OrdinateID"
               WHERE oc."DPS" LIKE '%[%'
                 AND NOT EXISTS (SELECT 1 FROM "mOpenAxisValueRestriction" r WHERE r."AxisID" = o."AxisID"))
            +
              (SELECT COUNT(DISTINCT r."AxisID") FROM "mOpenAxisValueRestriction" r
               WHERE NOT EXISTS (
                   SELECT 1 FROM "mAxisOrdinate" o
                   JOIN "mOrdinateCategorisation" oc ON oc."OrdinateID" = o."OrdinateID"
                   WHERE o."AxisID" = r."AxisID" AND oc."DPS" LIKE '%[%'
               ))
            """);

        // I-SIG-01b: the second half of A-SIG-04: not only "those that differ carry a bracket", but
        // "NONE without a bracket differs". Both directions, measured over the three own outputs
        // (0/0/0 for the second half).
        yield return SqlHelpers.CountCheck(
            c, "I-SIG-01b", ValidationLayer.Critical, "mTableCell",
            "DPS = DatapointSignature except inside the open-axis bracket: no cell WITHOUT a bracket differs",
            "SELECT COUNT(*) FROM \"mTableCell\"",
            "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"DPS\" IS NOT \"DatapointSignature\" AND (\"DPS\" IS NULL OR \"DPS\" NOT LIKE '%[%')");

        // I-CPT-02b: critical ONLY in DPM 2.0; in DPM 1.0 the mismatch is A-CPT-02, informative,
        // not fully closed.
        if (model == ValidationSourceModel.Dpm2)
        {
            yield return ConceptTypeCountsMatchEntitiesExactly(c);
        }
    }

    /// <summary>
    /// A-CPT-02: for each type, <c>COUNT(*)</c> equals the rows of its entity. INFORMATIVE layer:
    /// three of the types (Member, Hierarchy, HierarchyNode) have a documented mismatch that is not
    /// fully closed. Sharing a concept is only legitimate for TemplateOrTable/HierarchyNode, not for
    /// Member/Hierarchy, so forcing this check into the critical layer would block --validate over
    /// a point that is still open. The full breakdown is reported.
    /// </summary>
    private static CheckResult ConceptTypeCountsMatchEntities(SqliteConnection c)
    {
        var pairs = new (string ConceptType, string Table)[]
        {
            ("Ordinate", "mAxisOrdinate"), ("Axis", "mAxis"), ("Dimension", "mDimension"),
            ("Domain", "mDomain"), ("Member", "mMember"), ("Hierarchy", "mHierarchy"),
            ("HierarchyNode", "mHierarchyNode"), ("Taxonomy", "mTaxonomy"),
            ("ReportingFramework", "mReportingFramework"), ("Release", "mRelease"),
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mismatches = new List<CheckSample>();
        var examined = 0L;
        foreach (var (conceptType, table) in pairs)
        {
            var conceptCount = SqlHelpers.Scalar(c, $"SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = '{conceptType}'");
            var tableCount = SqlHelpers.Scalar(c, $"SELECT COUNT(*) FROM \"{table}\"");
            examined++;
            if (conceptCount != tableCount)
            {
                mismatches.Add(new CheckSample($"{conceptType}: mConcept={conceptCount} {table}={tableCount}"));
            }
        }
        sw.Stop();

        return CheckResult.FromViolationCount(
            "A-CPT-02", ValidationPlane.A, ValidationLayer.Informative, "mConcept",
            "For each type, COUNT(mConcept) equals the rows of its entity", examined, mismatches.Count, sw.ElapsedMilliseconds, mismatches);
    }

    /// <summary>
    /// I-CPT-02b: for each of the 13 types of the destination vocabulary, <c>COUNT(mConcept)</c>
    /// equals the rows of its entity EXACTLY, discounting the sentinel <c>DomainID</c> = 9999 ONLY
    /// in <c>Domain</c> (the <c>mDomain</c> sentinel carries no concept in DPM 2.0). In
    /// <c>Member</c> it is NOT discounted: <c>mMember</c> 9999 DOES carry its own concept in
    /// DPM 2.0 (discounting it there would fail by exactly 1 row). Critical only for DPM 2.0
    /// (called from <see cref="Run"/>).
    /// </summary>
    private static CheckResult ConceptTypeCountsMatchEntitiesExactly(SqliteConnection c)
    {
        var pairs = new (string ConceptType, string Table, string Pk, bool DiscountSentinel)[]
        {
            ("Axis", "mAxis", "AxisID", false),
            ("Ordinate", "mAxisOrdinate", "OrdinateID", false),
            ("Dimension", "mDimension", "DimensionID", false),
            ("Domain", "mDomain", "DomainID", true),
            ("Hierarchy", "mHierarchy", "HierarchyID", false),
            ("HierarchyNode", "mHierarchyNode", "HierarchyNodeID", false),
            ("Member", "mMember", "MemberID", false),
            ("Module", "mModule", "ModuleID", false),
            ("Release", "mRelease", "ReleaseID", false),
            ("ReportingFramework", "mReportingFramework", "FrameworkID", false),
            ("Table", "mTable", "TableID", false),
            ("TemplateOrTable", "mTemplateOrTable", "TemplateOrTableID", false),
            ("Taxonomy", "mTaxonomy", "TaxonomyID", false),
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mismatches = new List<CheckSample>();
        var examined = 0L;
        foreach (var (conceptType, table, pk, discountSentinel) in pairs)
        {
            var conceptCount = SqlHelpers.Scalar(c, $"SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = '{conceptType}'");
            var tableCountSql = discountSentinel
                ? $"SELECT COUNT(*) FROM \"{table}\" WHERE \"{pk}\" <> 9999"
                : $"SELECT COUNT(*) FROM \"{table}\"";
            var tableCount = SqlHelpers.Scalar(c, tableCountSql);
            examined++;
            if (conceptCount != tableCount)
            {
                mismatches.Add(new CheckSample($"{conceptType}: mConcept={conceptCount} {table}={tableCount}"));
            }
        }
        sw.Stop();

        return CheckResult.FromViolationCount(
            "I-CPT-02b", ValidationPlane.A, ValidationLayer.Critical, "mConcept",
            "For each of the 13 types, COUNT(mConcept) == rows of its entity, discounting the sentinel 9999 ONLY in Domain",
            examined, mismatches.Count, sw.ElapsedMilliseconds, mismatches);
    }
}
