using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.Checks;

/// <summary>
/// Referential integrity and uniqueness of business keys. Plane A, critical layer unless
/// stated otherwise. It does not look at any reference.
///
/// A-UNQ-12 (<c>mTable.TableCode</c> is NOT unique) is an ANTI-invariant: it is deliberately not
/// checked here (checking it would fail by design).
///
/// <c>I-REF-04</c>...<c>I-REF-16</c> and <c>I-UNQ-07b</c>/<c>I-UNQ-08</c> are FKs and identities
/// not declared by the schema. They were measured at 0 violations over the THREE own outputs
/// (DPM 1.0 --all, DPM 2.0 4.2 --all, DPM 2.0 4.3 --all) before being registered: external
/// references are not enough, the own output decides.
///
/// <c>I-UNQ-08</c> first measured 15 on DPM 1.0 with a reading that was TOO strict ("at most one
/// dictionary ROW"): the 15 are pairs of <c>mHierarchy</c> sharing a <c>ConceptID</c>, already
/// covered by the same phenomenon as <c>A-HIE-07</c>. The correct reading is "at most one entity
/// TYPE" (mDomain/mMember/mDimension/mHierarchy, not row by row): with it the result is 0/0/0 on
/// the three outputs, and that is the one implemented.
///
/// <c>I-REF-01</c> (every <c>mHierarchy</c> has >=1 node) DOES fail on our own output
/// (14/977 DPM 1.0, 14/1154 4.2, 15/1185 4.3): they are the SAME business hierarchies in all
/// three. It drops to the INFORMATIVE layer with a declared census and is not registered as
/// critical. <c>I-REF-15</c> (every <c>mModule</c> has >=1 <c>mModuleBusinessTemplate</c>) and
/// <c>I-XBR-02</c> (local part of <c>DimensionXBRLCode</c> == <c>DimensionCode</c>) DO fail on
/// DPM 1.0 (I-REF-15: 3/415, documentation-only P3_*_DOCS modules; I-XBR-02: 5/864, five
/// dimensions with an embedded prefix in DimensionCode, 'eba_dim_4.0:EXC' vs
/// DimensionCode='qEC:EXC'). They are registered as CRITICAL only for DPM 2.0 (where they give
/// 0/0), not for DPM 1.0.
///
/// <c>I-XBR-01c</c> is the EXCEPTION to that pattern: its 50 violations on DPM 1.0 ARE exactly a
/// known, fully characterised anomaly of the source, with a closed census. Critical in BOTH
/// models; in DPM 1.0 the 50 are registered as an exception NAMED by exact business key in the
/// single <see cref="KnownExceptions"/> registry. It is not scoped out of the model: the only
/// remaining risk for DPM 1.0 is a regression introduced by us, and scoping the check out of
/// DPM 1.0 would stop looking for it.
/// </summary>
public static class IntegrityChecks
{
    private static CheckResult Orphans(SqliteConnection c, string id, string childTable, string childColumn, string parentTable, string parentColumn, ValidationLayer layer = ValidationLayer.Critical)
    {
        var totalSql = $"SELECT COUNT(*) FROM \"{childTable}\"";
        var violationSql =
            $"""
            SELECT DISTINCT "{childColumn}" FROM "{childTable}" c
            WHERE c."{childColumn}" IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM "{parentTable}" p WHERE p."{parentColumn}" = c."{childColumn}")
            LIMIT 10000
            """;
        return SqlHelpers.ViolationCheck(c, id, layer, childTable, $"{childTable}.{childColumn} -> {parentTable}.{parentColumn}: no orphans", totalSql, violationSql);
    }

    public static IEnumerable<CheckResult> Run(SqliteConnection c, ValidationSourceModel model, List<KnownExceptions.Outcome> exceptionSink)
    {
        // ---- A-INT-01: foreign_key_check only reports the root sentinel (ParentTemplateOrTableID = 0) ----
        yield return ForeignKeyCheckOnlyRootSentinel(c, model);

        // ---- A-INT-02: 0 orphans in the 17 dictionary FKs ----
        var dictionaryFks = new (string Child, string Col, string Parent, string ParentCol)[]
        {
            ("mDomain", "ConceptID", "mConcept", "ConceptID"),
            ("mMember", "ConceptID", "mConcept", "ConceptID"),
            ("mDimension", "ConceptID", "mConcept", "ConceptID"),
            ("mHierarchy", "ConceptID", "mConcept", "ConceptID"),
            ("mHierarchyNode", "ConceptID", "mConcept", "ConceptID"),
            ("mConceptTranslation", "ConceptID", "mConcept", "ConceptID"),
            ("mMember", "DomainID", "mDomain", "DomainID"),
            ("mDimension", "DomainID", "mDomain", "DomainID"),
            ("mHierarchy", "DomainID", "mDomain", "DomainID"),
            ("mDimension", "DefaultMemberID", "mMember", "MemberID"),
            ("mHierarchyNode", "MemberID", "mMember", "MemberID"),
            ("mHierarchyNode", "ParentMemberID", "mMember", "MemberID"),
            ("mMetric", "CorrespondingMemberID", "mMember", "MemberID"),
            ("mHierarchyNode", "HierarchyID", "mHierarchy", "HierarchyID"),
            ("mConcept", "OwnerID", "mOwner", "OwnerID"),
            ("mConcept", "ReleaseID", "mRelease", "ReleaseID"),
            ("mConceptTranslation", "LanguageID", "mLanguage", "LanguageID"),
        };
        var i = 1;
        foreach (var (child, col, parent, parentCol) in dictionaryFks)
        {
            yield return Orphans(c, $"A-INT-02.{i++}", child, col, parent, parentCol);
        }

        // ---- A-INT-03: 0 orphans in the 6 template/table FKs (excluding the 0 sentinel) ----
        yield return ParentTemplateOrTableNoOrphansExcludingRootSentinel(c);
        var templateFks = new (string Child, string Col, string Parent, string ParentCol)[]
        {
            ("mTemplateOrTable", "TaxonomyID", "mTaxonomy", "TaxonomyID"),
            ("mTemplateOrTable", "ConceptID", "mConcept", "ConceptID"),
            ("mTable", "ConceptID", "mConcept", "ConceptID"),
            ("mTaxonomyTable", "TaxonomyID", "mTaxonomy", "TaxonomyID"),
            ("mTaxonomyTable", "TableID", "mTable", "TableID"),
            ("mTaxonomyTable", "AnnotatedTableID", "mTemplateOrTable", "TemplateOrTableID"),
        };
        i = 1;
        foreach (var (child, col, parent, parentCol) in templateFks)
        {
            yield return Orphans(c, $"A-INT-03.{i++}", child, col, parent, parentCol);
        }

        // ---- A-INT-04: 0 orphans in the 5 module FKs ----
        var moduleFks = new (string Child, string Col, string Parent, string ParentCol)[]
        {
            ("mModule", "TaxonomyID", "mTaxonomy", "TaxonomyID"),
            ("mModule", "ConceptualModuleID", "mConceptualModule", "ConceptualModuleID"),
            ("mModule", "ConceptID", "mConcept", "ConceptID"),
            ("mModuleBusinessTemplate", "ModuleID", "mModule", "ModuleID"),
            ("mModuleBusinessTemplate", "BusinessTemplateID", "mTemplateOrTable", "TemplateOrTableID"),
        };
        i = 1;
        foreach (var (child, col, parent, parentCol) in moduleFks)
        {
            yield return Orphans(c, $"A-INT-04.{i++}", child, col, parent, parentCol);
        }

        // ---- A-INT-05: 0 mCellPosition orphans in both directions ----
        yield return Orphans(c, "A-INT-05.1", "mCellPosition", "CellID", "mTableCell", "CellID");
        yield return Orphans(c, "A-INT-05.2", "mCellPosition", "OrdinateID", "mAxisOrdinate", "OrdinateID");

        // ---- A-INT-06: 0 mOrdinateCategorisation orphans in all three directions + mMember->mDomain ----
        yield return Orphans(c, "A-INT-06.1", "mOrdinateCategorisation", "OrdinateID", "mAxisOrdinate", "OrdinateID");
        yield return Orphans(c, "A-INT-06.2", "mOrdinateCategorisation", "DimensionID", "mDimension", "DimensionID");
        yield return Orphans(c, "A-INT-06.3", "mOrdinateCategorisation", "MemberID", "mMember", "MemberID");
        yield return Orphans(c, "A-INT-06.4", "mMember", "DomainID", "mDomain", "DomainID");

        // ---- A-INT-07: no mConcept without an object that uses it ----
        yield return ConceptsWithoutAnyUser(c);

        // ---- A-INT-08: mAxisOrdinate.RelatedDimensionTableId never dangling and never 0 ----
        yield return SqlHelpers.CountCheck(
            c, "A-INT-08", ValidationLayer.Critical, "mAxisOrdinate",
            "RelatedDimensionTableId never dangling or zero (FK without data = NULL)",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\"",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"RelatedDimensionTableId\" = 0 OR (\"RelatedDimensionTableId\" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM \"mTable\" t WHERE t.\"TableID\" = \"mAxisOrdinate\".\"RelatedDimensionTableId\"))");

        // ---- A-UNQ-01: DomainCode and DomainXBRLCode unique ----
        yield return DuplicateGroups(c, "A-UNQ-01.1", "mDomain", "DomainCode", "DomainCode unique");
        yield return DuplicateGroups(c, "A-UNQ-01.2", "mDomain", "DomainXBRLCode", "DomainXBRLCode unique");

        // ---- A-UNQ-02: DimensionXBRLCode unique ----
        yield return DuplicateGroups(c, "A-UNQ-02", "mDimension", "DimensionXBRLCode", "DimensionXBRLCode unique");

        // ---- A-UNQ-03: MemberXBRLCode unique. Known source anomaly: drops to the INFORMATIVE layer, with an exact census ----
        yield return MemberXbrlCodeUniqueness(c);
        yield return MemberCodeColonCensus(c, model);

        // ---- A-UNQ-04: (DomainID, MemberCode) unique ----
        yield return DuplicateGroups(c, "A-UNQ-04", "mMember", "\"DomainID\", \"MemberCode\"", "(DomainID, MemberCode) unique", isExpression: true);

        // ---- A-UNQ-05: (DomainID, HierarchyCode) unique ----
        yield return DuplicateGroups(c, "A-UNQ-05", "mHierarchy", "\"DomainID\", \"HierarchyCode\"", "(DomainID, HierarchyCode) unique", isExpression: true);

        // ---- A-UNQ-06: TaxonomyCode, FrameworkCode, ReleaseCode unique ----
        yield return DuplicateGroups(c, "A-UNQ-06.1", "mTaxonomy", "TaxonomyCode", "TaxonomyCode unique");
        yield return DuplicateGroups(c, "A-UNQ-06.2", "mReportingFramework", "FrameworkCode", "FrameworkCode unique");
        yield return DuplicateGroups(c, "A-UNQ-06.3", "mRelease", "ReleaseCode", "ReleaseCode unique");

        // ---- A-UNQ-07: BusinessCode unique WITHIN its TableID (NOT globally) ----
        yield return DuplicateGroups(c, "A-UNQ-07", "mTableCell", "\"TableID\", \"BusinessCode\"", "BusinessCode unique within its TableID", isExpression: true);

        // ---- A-UNQ-08: (AxisID, OrdinateCode) unique (NOT (TableID, OrdinateCode): X and Y share codes) ----
        yield return DuplicateGroups(c, "A-UNQ-08", "mAxisOrdinate", "\"AxisID\", \"OrdinateCode\"", "(AxisID, OrdinateCode) unique", isExpression: true);

        // ---- A-UNQ-09: (OrdinateID, DimensionID) does not collide ----
        yield return SqlHelpers.CountCheck(
            c, "A-UNQ-09", ValidationLayer.Critical, "mOrdinateCategorisation", "(OrdinateID, DimensionID) does not collide",
            "SELECT COUNT(*) FROM \"mOrdinateCategorisation\"",
            "SELECT (SELECT COUNT(*) FROM \"mOrdinateCategorisation\") - (SELECT COUNT(*) FROM (SELECT DISTINCT \"OrdinateID\",\"DimensionID\" FROM \"mOrdinateCategorisation\"))");

        // ---- A-UNQ-10: (TaxonomyID, TemplateOrTableCode, Level) unique ----
        yield return DuplicateGroups(c, "A-UNQ-10", "mTemplateOrTable", "\"TaxonomyID\", \"TemplateOrTableCode\", \"Level\"", "(TaxonomyID, TemplateOrTableCode, Level) unique", isExpression: true);

        // ---- A-UNQ-11: ModuleCode unique WITHIN its taxonomy (NOT globally) ----
        yield return DuplicateGroups(c, "A-UNQ-11", "mModule", "\"TaxonomyID\", \"ModuleCode\"", "ModuleCode unique within its taxonomy", isExpression: true);

        // ---- A-VAC-02: mOwnerParent has exactly 1 row ----
        yield return ExactRowCount(c, "A-VAC-02", "mOwnerParent", 1);

        // ---- A-VAC-03: the schema has exactly 45 tables and 12 sqlite_autoindex_* indexes ----
        yield return SchemaTableAndIndexCount(c);

        // ---- The FKs that the schema does NOT declare. Measured 0/0/0 over our three outputs
        // (DPM 1.0 --all, DPM 2.0 4.2 --all, DPM 2.0 4.3 --all) before being registered. ----
        yield return Orphans(c, "I-REF-04", "mMetric", "ReferencedHierarchyID", "mHierarchy", "HierarchyID");
        yield return Orphans(c, "I-REF-05", "mMetric", "ReferencedDomainID", "mDomain", "DomainID");
        yield return ReferencedHierarchyBelongsToReferencedDomain(c);
        yield return DomainUnionFksValidAndDistinct(c);
        yield return Orphans(c, "I-REF-08", "mOpenAxisValueRestriction", "HierarchyID", "mHierarchy", "HierarchyID");
        yield return Orphans(c, "I-REF-09", "mTableCell", "TableID", "mTable", "TableID");
        yield return Orphans(c, "I-REF-10.1", "mTableAxis", "TableID", "mTable", "TableID");
        yield return Orphans(c, "I-REF-10.2", "mTableAxis", "AxisID", "mAxis", "AxisID");
        yield return NoOrphanParents(c, "I-REF-11", "mAxis", "AxisID", "mTableAxis", "AxisID", "Every axis is linked to some table (mTableAxis)");
        yield return NoOrphanParents(c, "I-REF-12", "mTable", "TableID", "mTableAxis", "TableID", "Every table has at least one axis (mTableAxis)");
        yield return NoOrphanParents(c, "I-REF-13", "mTable", "TableID", "mTableCell", "TableID", "Every table has at least one cell (mTableCell)");
        yield return MetMembersHaveMetricRow(c);
        yield return NonAbstractOrdinatesHaveCellPosition(c);

        // ---- I-UNQ-07b: (taxonomy, TableCode) unique: the derivable counterpart of the
        // anti-invariant A-UNQ-12 (mTable.TableCode is NOT unique by itself) ----
        yield return SqlHelpers.CountCheck(
            c, "I-UNQ-07b", ValidationLayer.Critical, "mTaxonomyTable", "(TaxonomyID, TableCode) unique, via mTaxonomyTable/mTable",
            "SELECT COUNT(*) FROM \"mTaxonomyTable\"",
            """
            SELECT COUNT(*) FROM (
                SELECT tt."TaxonomyID", t."TableCode" FROM "mTaxonomyTable" tt
                JOIN "mTable" t ON t."TableID" = tt."TableID"
                GROUP BY tt."TaxonomyID", t."TableCode" HAVING COUNT(*) > 1)
            """);

        // ---- I-UNQ-08: a ConceptID is used by at most ONE entity TYPE of the dictionary
        // (mDomain/mMember/mDimension/mHierarchy), not "one row": counting rows would have caught
        // the mHierarchy pairs that A-HIE-07 already covers. 0/0/0 with this reading. ----
        yield return SqlHelpers.CountCheck(
            c, "I-UNQ-08", ValidationLayer.Critical, "mConcept", "A ConceptID is used by at most one dictionary entity TYPE (domain, member, dimension, hierarchy)",
            "SELECT COUNT(*) FROM \"mConcept\"",
            """
            SELECT COUNT(*) FROM (
                SELECT "ConceptID" FROM (
                    SELECT DISTINCT "ConceptID" FROM "mDomain" WHERE "ConceptID" IS NOT NULL
                    UNION ALL SELECT DISTINCT "ConceptID" FROM "mMember" WHERE "ConceptID" IS NOT NULL
                    UNION ALL SELECT DISTINCT "ConceptID" FROM "mDimension" WHERE "ConceptID" IS NOT NULL
                    UNION ALL SELECT DISTINCT "ConceptID" FROM "mHierarchy" WHERE "ConceptID" IS NOT NULL
                ) GROUP BY "ConceptID" HAVING COUNT(*) > 1)
            """);

        // ---- I-REF-01: "every mHierarchy has >=1 node" is NOT an invariant of ours: 14 in
        // DPM 1.0, 14 in 4.2, 15 in 4.3, the SAME business hierarchies. INFORMATIVE layer with a
        // declared census (domain:code), not critical. ----
        yield return HierarchiesWithoutNodesDeclaredCensus(c);

        // ---- I-REF-15/I-XBR-02: fail on DPM 1.0, hold on DPM 2.0 (0/0): critical ONLY for
        // DPM 2.0. ----
        if (model == ValidationSourceModel.Dpm2)
        {
            yield return NoOrphanParents(c, "I-REF-15", "mModule", "ModuleID", "mModuleBusinessTemplate", "ModuleID", "Every module has at least one mModuleBusinessTemplate (critical only in DPM 2.0: fails 3/415 in DPM 1.0)");
            yield return DimensionXbrlCodeLocalPartMatchesDimensionCode(c);
        }

        // ---- I-XBR-01c: NOT scoped to DPM 2.0. Its 50 violations on DPM 1.0 ARE exactly a
        // documented source anomaly with a closed census. The only remaining risk for DPM 1.0 is
        // a regression introduced by us, and scoping the check out of that model would stop
        // looking for it. Critical in BOTH models; in DPM 1.0 the 50 are registered as an
        // exception NAMED by exact business key, the same pattern already used by
        // A-DIC-11/DPM 1.0: if the set grows (a 51st appears) or shrinks, the check fails. ----
        yield return MemberXbrlCodeLocalPartMatchesMemberCode(c, model, exceptionSink);
    }

    /// <summary>
    /// I-REF-01: DECLARED, non-critical census of hierarchies without any <c>mHierarchyNode</c>.
    /// A zero is a property of a third-party export, not of the format: here the count and the
    /// business keys are reported, and the number is a signal if it grows without explanation
    /// (its natural place is the <c>GeneratedCensus</c> mode).
    /// </summary>
    private static CheckResult HierarchiesWithoutNodesDeclaredCensus(SqliteConnection c)
    {
        var totalSql = "SELECT COUNT(*) FROM \"mHierarchy\"";
        var violationSql =
            """
            SELECT d."DomainCode" || ':' || h."HierarchyCode" FROM "mHierarchy" h
            JOIN "mDomain" d ON d."DomainID" = h."DomainID"
            WHERE NOT EXISTS (SELECT 1 FROM "mHierarchyNode" n WHERE n."HierarchyID" = h."HierarchyID")
            """;
        return SqlHelpers.ViolationCheck(
            c, "I-REF-01", ValidationLayer.Informative, "mHierarchy",
            "DECLARED census of hierarchies without nodes (same business hierarchies in the three outputs)",
            totalSql, violationSql);
    }

    /// <summary>
    /// I-XBR-01c: the local part (after the first ':') of <c>MemberXBRLCode</c> is the
    /// <c>MemberCode</c>. Critical in BOTH models: in DPM 2.0 it gives 0/0 without exception; in
    /// DPM 1.0 the only 50 violations are the known source anomaly, registered in the single
    /// <see cref="KnownExceptions"/> registry as a NAMED exception: if a 51st appears (growth) or
    /// one of the 50 stops violating (shrinkage, a sign that the Access changed), the check
    /// fails. It is never left unchecked.
    /// </summary>
    private static CheckResult MemberXbrlCodeLocalPartMatchesMemberCode(SqliteConnection c, ValidationSourceModel model, List<KnownExceptions.Outcome> exceptionSink)
    {
        if (model == ValidationSourceModel.Dpm2)
        {
            return SqlHelpers.CountCheck(
                c, "I-XBR-01c", ValidationLayer.Critical, "mMember", "The local part of MemberXBRLCode (after ':') is the MemberCode",
                "SELECT COUNT(*) FROM \"mMember\" WHERE \"MemberXBRLCode\" IS NOT NULL",
                """
                SELECT COUNT(*) FROM "mMember"
                WHERE "MemberXBRLCode" IS NOT NULL
                  AND substr("MemberXBRLCode", instr("MemberXBRLCode", ':') + 1) IS NOT "MemberCode"
                """);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var total = SqlHelpers.Scalar(c, "SELECT COUNT(*) FROM \"mMember\" WHERE \"MemberXBRLCode\" IS NOT NULL");
        var rows = SqlHelpers.Rows(
            c,
            """
            SELECT d."DomainCode" || ':' || m."MemberCode"
            FROM "mMember" m
            JOIN "mDomain" d ON d."DomainID" = m."DomainID"
            WHERE m."MemberXBRLCode" IS NOT NULL
              AND substr(m."MemberXBRLCode", instr(m."MemberXBRLCode", ':') + 1) IS NOT m."MemberCode"
            """,
            1);
        sw.Stop();

        var violating = rows.Select(r => r[0]!).ToHashSet(StringComparer.Ordinal);
        var (unresolved, outcomes) = KnownExceptions.ApplyOriginAnomalyExceptions("I-XBR-01c", violating, ValidationPlane.A);
        exceptionSink.AddRange(outcomes);
        var samples = unresolved.Select(k => new CheckSample(k)).ToList();

        return CheckResult.FromViolationCount(
            "I-XBR-01c", ValidationPlane.A, ValidationLayer.Critical, "mMember",
            "The local part of MemberXBRLCode is the MemberCode, except the 50 of the known source anomaly (named exception)",
            total, samples.Count, sw.ElapsedMilliseconds, samples);
    }

    /// <summary>I-XBR-02: the local part (after the first ':') of DimensionXBRLCode is the DimensionCode.</summary>
    private static CheckResult DimensionXbrlCodeLocalPartMatchesDimensionCode(SqliteConnection c) =>
        SqlHelpers.CountCheck(
            c, "I-XBR-02", ValidationLayer.Critical, "mDimension", "The local part of DimensionXBRLCode (after ':') is the DimensionCode",
            "SELECT COUNT(*) FROM \"mDimension\" WHERE \"DimensionXBRLCode\" IS NOT NULL",
            """
            SELECT COUNT(*) FROM "mDimension"
            WHERE "DimensionXBRLCode" IS NOT NULL
              AND substr("DimensionXBRLCode", instr("DimensionXBRLCode", ':') + 1) IS NOT "DimensionCode"
            """);

    /// <summary>The inverse of <see cref="Orphans"/>: no <paramref name="parentTable"/> is left without at least one row of <paramref name="childTable"/> referencing it.</summary>
    private static CheckResult NoOrphanParents(SqliteConnection c, string id, string parentTable, string parentColumn, string childTable, string childColumn, string statement, ValidationLayer layer = ValidationLayer.Critical)
    {
        var totalSql = $"SELECT COUNT(*) FROM \"{parentTable}\"";
        var violationSql =
            $"""
            SELECT DISTINCT p."{parentColumn}" FROM "{parentTable}" p
            WHERE NOT EXISTS (SELECT 1 FROM "{childTable}" ch WHERE ch."{childColumn}" = p."{parentColumn}")
            LIMIT 10000
            """;
        return SqlHelpers.ViolationCheck(c, id, layer, parentTable, statement, totalSql, violationSql);
    }

    /// <summary>I-REF-06: the hierarchy referenced by <c>mMetric</c> belongs to the domain that the same <c>mMetric</c> references.</summary>
    private static CheckResult ReferencedHierarchyBelongsToReferencedDomain(SqliteConnection c) =>
        SqlHelpers.CountCheck(
            c, "I-REF-06", ValidationLayer.Critical, "mMetric", "The referenced hierarchy belongs to the referenced domain (same mMetric)",
            "SELECT COUNT(*) FROM \"mMetric\" WHERE \"ReferencedHierarchyID\" IS NOT NULL AND \"ReferencedDomainID\" IS NOT NULL",
            """
            SELECT COUNT(*) FROM "mMetric" m
            JOIN "mHierarchy" h ON h."HierarchyID" = m."ReferencedHierarchyID"
            WHERE m."ReferencedHierarchyID" IS NOT NULL AND m."ReferencedDomainID" IS NOT NULL
              AND h."DomainID" <> m."ReferencedDomainID"
            """);

    /// <summary>I-REF-07: in <c>mDomainUnion</c> both <c>DomainID</c> exist and differ.</summary>
    private static CheckResult DomainUnionFksValidAndDistinct(SqliteConnection c) =>
        SqlHelpers.CountCheck(
            c, "I-REF-07", ValidationLayer.Critical, "mDomainUnion", "UnionDomainID and UnitedDomainID exist in mDomain and differ",
            "SELECT COUNT(*) FROM \"mDomainUnion\"",
            """
            SELECT COUNT(*) FROM "mDomainUnion" du
            WHERE NOT EXISTS (SELECT 1 FROM "mDomain" d WHERE d."DomainID" = du."UnionDomainID")
               OR NOT EXISTS (SELECT 1 FROM "mDomain" d WHERE d."DomainID" = du."UnitedDomainID")
               OR du."UnionDomainID" = du."UnitedDomainID"
            """);

    /// <summary>I-REF-14: every member of the MET domain has a row in <c>mMetric</c>.</summary>
    private static CheckResult MetMembersHaveMetricRow(SqliteConnection c) =>
        SqlHelpers.CountCheck(
            c, "I-REF-14", ValidationLayer.Critical, "mMember", "Every member of the MET domain has a row in mMetric",
            "SELECT COUNT(*) FROM \"mMember\" m JOIN \"mDomain\" d ON d.\"DomainID\" = m.\"DomainID\" WHERE d.\"DomainCode\" = 'MET'",
            """
            SELECT COUNT(*) FROM "mMember" m
            JOIN "mDomain" d ON d."DomainID" = m."DomainID"
            WHERE d."DomainCode" = 'MET'
              AND NOT EXISTS (SELECT 1 FROM "mMetric" met WHERE met."CorrespondingMemberID" = m."MemberID")
            """);

    /// <summary>I-REF-16: every NON-abstract ordinate has at least one cell position (mCellPosition).</summary>
    private static CheckResult NonAbstractOrdinatesHaveCellPosition(SqliteConnection c) =>
        SqlHelpers.CountCheck(
            c, "I-REF-16", ValidationLayer.Critical, "mAxisOrdinate", "Every NON-abstract ordinate has at least one cell position",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"IsAbstractHeader\" = 0",
            """
            SELECT COUNT(*) FROM "mAxisOrdinate" o
            WHERE o."IsAbstractHeader" = 0
              AND NOT EXISTS (SELECT 1 FROM "mCellPosition" cp WHERE cp."OrdinateID" = o."OrdinateID")
            """);

    /// <summary>
    /// The root sentinel <c>ParentTemplateOrTableID = 0</c> is used by <c>TableGroup/Level=1</c> IN
    /// BOTH MODELS, and ADDITIONALLY in DPM 2.0 by orphan <c>BusinessTable</c> rows (measured:
    /// 111 + 47 in 4.2, 111 + 48 in our output). The statement admits both and no other row; in
    /// DPM 1.0 there are no orphans of that kind, so filtering by
    /// <c>ParentTemplateOrTableID = 0</c> alone would already be equivalent to filtering by
    /// <c>TableGroup/Level=1</c>, but the explicit DPM 1.0 form is kept so that its behaviour is
    /// unchanged by construction.
    /// </summary>
    private static CheckResult ForeignKeyCheckOnlyRootSentinel(SqliteConnection c, ValidationSourceModel model)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var violations = SqlHelpers.Rows(c, "SELECT \"table\", \"rowid\" FROM pragma_foreign_key_check", 2);
        var expectedIdsSql = model == ValidationSourceModel.Dpm2
            ? "SELECT \"TemplateOrTableID\" FROM \"mTemplateOrTable\" WHERE \"ParentTemplateOrTableID\" = 0"
            : "SELECT \"TemplateOrTableID\" FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'TableGroup' AND \"Level\" = 1";
        var expectedIds = SqlHelpers.Rows(c, expectedIdsSql, 1)
            .Select(r => r[0]!)
            .ToHashSet(StringComparer.Ordinal);

        var matchedExpected = new HashSet<string>(StringComparer.Ordinal);
        var unexpected = new List<CheckSample>();
        foreach (var v in violations)
        {
            var table = v[0]!;
            var rowid = v[1]!;
            if (table == "mTemplateOrTable" && expectedIds.Contains(rowid))
            {
                matchedExpected.Add(rowid);
            }
            else
            {
                unexpected.Add(new CheckSample($"{table}#{rowid}"));
            }
        }

        var missingExpected = expectedIds.Except(matchedExpected).Count();
        sw.Stop();

        var failed = unexpected.Count + missingExpected;
        var statement = model == ValidationSourceModel.Dpm2
            ? "pragma_foreign_key_check returns EXACTLY the rows with ParentTemplateOrTableID=0 (TableGroup/Level=1 and orphan BusinessTable), and no other"
            : "pragma_foreign_key_check returns EXACTLY the TableGroup/Level=1 rows (sentinel ParentTemplateOrTableID=0), and no other";
        return CheckResult.FromViolationCount(
            "A-INT-01", ValidationPlane.A, ValidationLayer.Critical, "mTemplateOrTable",
            statement,
            examined: violations.Count, failed: failed, sw.ElapsedMilliseconds, unexpected);
    }

    private static CheckResult ParentTemplateOrTableNoOrphansExcludingRootSentinel(SqliteConnection c)
    {
        var totalSql = "SELECT COUNT(*) FROM \"mTemplateOrTable\"";
        var violationSql =
            """
            SELECT DISTINCT c."TemplateOrTableID" FROM "mTemplateOrTable" c
            WHERE c."ParentTemplateOrTableID" IS NOT NULL AND c."ParentTemplateOrTableID" <> 0
              AND NOT EXISTS (SELECT 1 FROM "mTemplateOrTable" p WHERE p."TemplateOrTableID" = c."ParentTemplateOrTableID")
            """;
        return SqlHelpers.ViolationCheck(
            c, "A-INT-03.0", ValidationLayer.Critical, "mTemplateOrTable",
            "ParentTemplateOrTableID without orphans (excluding the root sentinel 0)", totalSql, violationSql);
    }

    private static CheckResult ConceptsWithoutAnyUser(SqliteConnection c)
    {
        const string usedIds =
            """
            SELECT "ConceptID" FROM "mDomain" WHERE "ConceptID" IS NOT NULL
            UNION SELECT "ConceptID" FROM "mMember" WHERE "ConceptID" IS NOT NULL
            UNION SELECT "ConceptID" FROM "mDimension" WHERE "ConceptID" IS NOT NULL
            UNION SELECT "ConceptID" FROM "mHierarchy" WHERE "ConceptID" IS NOT NULL
            UNION SELECT "ConceptID" FROM "mHierarchyNode" WHERE "ConceptID" IS NOT NULL
            UNION SELECT "ConceptID" FROM "mAxis" WHERE "ConceptID" IS NOT NULL
            UNION SELECT "ConceptID" FROM "mAxisOrdinate" WHERE "ConceptID" IS NOT NULL
            UNION SELECT "ConceptID" FROM "mTemplateOrTable" WHERE "ConceptID" IS NOT NULL
            UNION SELECT "ConceptID" FROM "mTable" WHERE "ConceptID" IS NOT NULL
            UNION SELECT "ConceptID" FROM "mModule" WHERE "ConceptID" IS NOT NULL
            UNION SELECT "ConceptID" FROM "mTaxonomy" WHERE "ConceptID" IS NOT NULL
            UNION SELECT "ConceptID" FROM "mRelease" WHERE "ConceptID" IS NOT NULL
            UNION SELECT "ConceptID" FROM "mReportingFramework" WHERE "ConceptID" IS NOT NULL
            UNION SELECT "ConceptID" FROM "mOwner" WHERE "ConceptID" IS NOT NULL
            UNION SELECT "ConceptID" FROM "mLanguage" WHERE "ConceptID" IS NOT NULL
            """;
        var totalSql = "SELECT COUNT(*) FROM \"mConcept\"";
        var violationSql =
            $"""
            SELECT "ConceptID" FROM "mConcept"
            WHERE "ConceptID" NOT IN ({usedIds})
            LIMIT 10000
            """;
        return SqlHelpers.ViolationCheck(
            c, "A-INT-07", ValidationLayer.Critical, "mConcept", "No mConcept is left without an object that uses it", totalSql, violationSql);
    }

    private static CheckResult DuplicateGroups(SqliteConnection c, string id, string table, string columns, string statement, bool isExpression = false)
    {
        var columnList = isExpression ? columns : $"\"{columns}\"";
        var totalSql = $"SELECT COUNT(*) FROM \"{table}\"";
        var violationSql =
            $"""
            SELECT {columnList} FROM "{table}"
            WHERE {(isExpression ? columnList.Split(',')[0].Trim() : columnList)} IS NOT NULL
            GROUP BY {columnList} HAVING COUNT(*) > 1
            LIMIT 10000
            """;
        // Duplicate count: surplus rows per repeated group (not number of groups), so that
        // "Failed" reflects how many rows are in excess, consistently with the other checks.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var total = SqlHelpers.Scalar(c, totalSql);
        var groupCountSql =
            $"""
            SELECT COUNT(*) - 1 FROM "{table}"
            WHERE {(isExpression ? columnList.Split(',')[0].Trim() : columnList)} IS NOT NULL
            GROUP BY {columnList} HAVING COUNT(*) > 1
            """;
        var excessRows = SqlHelpers.Rows(c, groupCountSql, 1).Sum(r => long.Parse(r[0]!));
        var samples = SqlHelpers.Rows(c, violationSql, isExpression ? columns.Count(ch => ch == ',') + 1 : 1)
            .Select(r => new CheckSample(string.Join("|", r)))
            .ToList();
        sw.Stop();

        return CheckResult.FromViolationCount(id, ValidationPlane.A, ValidationLayer.Critical, table, statement, total, excessRows, sw.ElapsedMilliseconds, samples);
    }

    private static CheckResult MemberXbrlCodeUniqueness(SqliteConnection c)
    {
        var totalSql = "SELECT COUNT(*) FROM \"mMember\"";
        var violationSql =
            """
            SELECT "MemberXBRLCode" FROM "mMember"
            WHERE "MemberXBRLCode" IS NOT NULL
            GROUP BY "MemberXBRLCode" HAVING COUNT(*) > 1
            """;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var total = SqlHelpers.Scalar(c, totalSql);
        var groups = SqlHelpers.Rows(c, violationSql, 1);
        var duplicatedRowCount = SqlHelpers.Scalar(
            c,
            """
            SELECT COUNT(*) FROM "mMember" WHERE "MemberXBRLCode" IN (
                SELECT "MemberXBRLCode" FROM "mMember" WHERE "MemberXBRLCode" IS NOT NULL
                GROUP BY "MemberXBRLCode" HAVING COUNT(*) > 1)
            """);
        sw.Stop();

        var samples = groups.Take(10).Select(r => new CheckSample(r[0]!)).ToList();
        // Uniqueness of MemberXBRLCode is INFORMATIVE (49 duplicates are expected because of the
        // 50 members with a namespace prefix inside MemberCode: there is no evidence of an error
        // in the reference, it is an anomaly of the SOURCE).
        return CheckResult.FromViolationCount(
            "A-UNQ-03", ValidationPlane.A, ValidationLayer.Informative, "mMember",
            "MemberXBRLCode unique (drops to informative: 49 duplicates expected from a known source anomaly)",
            total, duplicatedRowCount, sw.ElapsedMilliseconds, samples);
    }

    /// <summary>
    /// Self-check that the derivable census of the SOURCE anomaly stays exact. In DPM 1.0: 50
    /// members with ':' in <c>MemberCode</c>, 49 duplicated <c>MemberXBRLCode</c>. It is CRITICAL:
    /// if the census changed, the Access has changed and the exception must be reviewed. In
    /// DPM 2.0 this is an anomaly of the DPM 1.0 Access that does not exist in the new source (0
    /// duplicates outside MET), so the expected census is 0 and 0 (measured 0/0 in the three
    /// references).
    /// </summary>
    private static CheckResult MemberCodeColonCensus(SqliteConnection c, ValidationSourceModel model)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var withColon = SqlHelpers.Scalar(c, "SELECT COUNT(*) FROM \"mMember\" WHERE \"MemberCode\" LIKE '%:%'");
        var duplicatedCodes = SqlHelpers.Scalar(
            c,
            """
            SELECT COUNT(*) FROM (
                SELECT "MemberXBRLCode" FROM "mMember" WHERE "MemberXBRLCode" IS NOT NULL
                GROUP BY "MemberXBRLCode" HAVING COUNT(*) > 1)
            """);
        sw.Stop();

        var (expectedWithColon, expectedDuplicates, statement) = model == ValidationSourceModel.Dpm2
            ? (0, 0, "In DPM 2.0 this is an anomaly of the DPM 1.0 Access: expected census 0 MemberCode with ':', 0 duplicated MemberXBRLCode")
            : (50, 49, "Exact census of the source anomaly (50 MemberCode with ':', 49 duplicated MemberXBRLCode)");

        var failed = (withColon == expectedWithColon ? 0 : 1) + (duplicatedCodes == expectedDuplicates ? 0 : 1);
        var samples = failed == 0
            ? []
            : new List<CheckSample> { new($"MemberCode with ':' = {withColon} (expected {expectedWithColon}); duplicated MemberXBRLCode = {duplicatedCodes} (expected {expectedDuplicates})") };
        return CheckResult.FromViolationCount(
            "A-UNQ-03-AO1", ValidationPlane.A, ValidationLayer.Critical, "mMember",
            statement,
            examined: 2, failed, sw.ElapsedMilliseconds, samples);
    }

    private static CheckResult ExactRowCount(SqliteConnection c, string id, string table, long expected) =>
        SqlHelpers.ExactMatch(c, id, ValidationLayer.Critical, table, $"{table} has exactly {expected} row(s)", $"SELECT COUNT(*) FROM \"{table}\"", expected);

    private static CheckResult SchemaTableAndIndexCount(SqliteConnection c)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tableCount = SqlHelpers.Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'");
        var autoIndexCount = SqlHelpers.Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name LIKE 'sqlite_autoindex_%'");
        sw.Stop();

        var failed = (tableCount == 45 ? 0 : 1) + (autoIndexCount == 12 ? 0 : 1);
        var samples = failed == 0 ? [] : new List<CheckSample> { new($"tables={tableCount} (expected 45), sqlite_autoindex_*={autoIndexCount} (expected 12)") };
        return CheckResult.FromViolationCount(
            "A-VAC-03", ValidationPlane.A, ValidationLayer.Critical, "(schema)",
            "The schema has exactly 45 tables and 12 sqlite_autoindex_* indexes (composite PKs)",
            examined: 2, failed, sw.ElapsedMilliseconds, samples);
    }
}
