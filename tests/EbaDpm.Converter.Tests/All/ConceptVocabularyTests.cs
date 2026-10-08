using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.All;

/// <summary>
/// Concept invariants A-CPT-01/02/04/05, A-DIC-14 and A-INT-07, plus the coverage of the
/// mConcept/mConceptTranslation/JsonBlob rules. These defects stayed hidden because the rest of
/// the suite only converted 1 or 8 of the 128 taxonomies.
///
/// Figures pinned for <c>--all</c> (128 taxonomies), CRITICAL layer unless stated otherwise.
/// </summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class ConceptVocabularyTests
{
    private readonly AllFixture _fixture;

    public ConceptVocabularyTests(AllFixture fixture)
    {
        _fixture = fixture;
    }

    private static readonly string[] DestinationVocabulary =
    [
        "Axis", "Ordinate", "Dimension", "Domain", "Hierarchy", "HierarchyNode",
        "Member", "Module", "Release", "ReportingFramework", "Table", "TemplateOrTable", "Taxonomy",
    ];

    /// <summary>
    /// A-CPT-01: <c>mConcept.ConceptType</c> belongs EXCLUSIVELY to the DESTINATION vocabulary.
    /// No source value (<c>TableVersion</c>, <c>TableGroup</c>, <c>Template</c>, or the
    /// <c>Table</c> of the <c>Access.Table</c> entity -- which must not exist here at all, see
    /// <see cref="NoConceptType_OutsideTheDestinationVocabulary"/>) survives.
    /// </summary>
    [DataFact]
    public void NoConceptType_OutsideTheDestinationVocabulary()
    {
        var placeholders = string.Join(",", DestinationVocabulary.Select(v => $"'{v}'"));
        var outsideCount = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            $"SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" NOT IN ({placeholders})");

        var samples = outsideCount > 0
            ? QueryHelpers.Rows(
                _fixture.GeneratedConnection,
                $"SELECT DISTINCT \"ConceptType\" FROM \"mConcept\" WHERE \"ConceptType\" NOT IN ({placeholders}) LIMIT 10",
                1)
            : [];

        Assert.True(
            outsideCount == 0,
            $"{outsideCount} concepts with a ConceptType outside the destination vocabulary. " +
            $"Values found: {string.Join(",", samples.Select(r => r[0]))}");
    }

    /// <summary>
    /// Exact census measured over --all: 0 <c>TableVersion</c>/<c>TableGroup</c>/<c>Template</c>
    /// (they must have been translated), exactly 2,561 <c>Table</c> and 1,347 <c>TemplateOrTable</c>.
    /// </summary>
    [DataTheory]
    [InlineData("TableVersion", 0)]
    [InlineData("TableGroup", 0)]
    [InlineData("Template", 0)]
    [InlineData("Table", 2561)]
    [InlineData("TemplateOrTable", 1347)]
    public void ConceptType_HasTheExactCensus_MeasuredOnAll(string conceptType, long expected)
    {
        var actual = QueryHelpers.Scalar(
            _fixture.GeneratedConnection, $"SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = '{conceptType}'");

        Assert.True(
            actual == expected,
            $"mConcept with ConceptType='{conceptType}': {actual} rows ({expected} expected, measured over --all).");
    }

    /// <summary>
    /// The 950 concepts of the <c>Access.Table</c> entity are NOT emitted (that entity produces no
    /// destination row, so its concept would describe nothing). The total of <c>mConcept</c>
    /// drops from 122,521 to exactly 121,571.
    /// </summary>
    [DataFact]
    public void MConcept_TotalRowCount_Is121571_AfterDroppingTheAccessTableEntity()
    {
        var total = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mConcept\"");

        Assert.True(
            total == 121_571,
            $"mConcept has {total} rows (exactly 121,571 expected over --all: " +
            "122,521 before dropping the concepts of the Access.Table entity, minus those 950).");
    }

    /// <summary>
    /// A-CPT-02: for each type, <c>COUNT(*)</c> equals the rows of its destination entity. Checked
    /// on the types that match exactly: <c>Ordinate</c>-<c>mAxisOrdinate</c>,
    /// <c>Axis</c>-<c>mAxis</c>, <c>Dimension</c>-<c>mDimension</c>, <c>Module</c>-<c>mModule</c>.
    /// </summary>
    [DataTheory]
    [InlineData("Ordinate", "mAxisOrdinate")]
    [InlineData("Axis", "mAxis")]
    [InlineData("Dimension", "mDimension")]
    [InlineData("Module", "mModule")]
    public void ConceptType_RowCount_MatchesItsOwnEntityTable(string conceptType, string entityTable)
    {
        var conceptCount = QueryHelpers.Scalar(
            _fixture.GeneratedConnection, $"SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = '{conceptType}'");
        var entityCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, $"SELECT COUNT(*) FROM \"{entityTable}\"");

        Assert.True(
            conceptCount == entityCount,
            $"mConcept.ConceptType='{conceptType}' has {conceptCount} rows versus {entityCount} in {entityTable} (A-CPT-02).");
    }

    /// <summary>A-CPT-04: no <c>ConceptType='ConceptualModule'</c> -- that entity reuses the ConceptID of the module and has no concept of its own.</summary>
    [DataFact]
    public void NoConceptType_IsConceptualModule()
    {
        var count = QueryHelpers.Scalar(
            _fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = 'ConceptualModule'");
        Assert.True(count == 0, $"{count} concepts with ConceptType='ConceptualModule' (0 expected, A-CPT-04).");
    }

    /// <summary>A-CPT-05: COUNT(ConceptType='Module') == |mModule|.</summary>
    [DataFact]
    public void ConceptType_Module_RowCount_EqualsModuleTableRowCount()
    {
        var conceptCount = QueryHelpers.Scalar(
            _fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = 'Module'");
        var moduleCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mModule\"");

        Assert.True(conceptCount == moduleCount, $"ConceptType='Module': {conceptCount} versus mModule: {moduleCount} (A-CPT-05).");
    }

    // ------------------------------------------------------------------
    // A-DIC-14: mConceptTranslation is complete. Only 3 concepts are left without a 'label',
    // and they are three named hierarchies whose HierarchyLabel is NULL in the Access itself.
    // ------------------------------------------------------------------

    [DataFact]
    public void ConceptsWithoutLabelTranslation_AreExactlyThreeHierarchies_WithNullHierarchyLabelInAccess()
    {
        var withoutLabel = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT c."ConceptID", c."ConceptType"
            FROM "mConcept" c
            WHERE NOT EXISTS (
                SELECT 1 FROM "mConceptTranslation" t
                WHERE t."ConceptID" = c."ConceptID" AND t."Role" = 'label'
            )
            """,
            2);

        Assert.True(
            withoutLabel.Count == 3,
            $"{withoutLabel.Count} concepts without a 'label' translation (exactly 3 expected, A-DIC-14 " +
            "- if it goes up, someone stopped seeding labels; if it drops to 0, this figure must be reviewed). " +
            $"ConceptID: {string.Join(",", withoutLabel.Select(r => r[0]))}");

        Assert.True(
            withoutLabel.All(r => r[1] == "Hierarchy"),
            $"Concepts without a 'label' must all be of type Hierarchy. Found: " +
            string.Join(",", withoutLabel.Select(r => $"{r[0]}:{r[1]}")));

        // And, on the Access itself (without looking at any reference): those hierarchies have a
        // NULL HierarchyLabel, which is exactly why there is nothing to seed.
        var hierarchyIds = withoutLabel
            .Select(r => int.Parse(
                QueryHelpers.Rows(_fixture.GeneratedConnection, $"SELECT \"HierarchyID\" FROM \"mHierarchy\" WHERE \"ConceptID\" = {r[0]}", 1)
                    .Single()[0]!))
            .ToList();

        var accessHierarchies = _fixture.AccessReader.ReadHierarchies()
            .Where(h => hierarchyIds.Contains(h.HierarchyId))
            .ToList();

        Assert.True(
            accessHierarchies.Count == 3 && accessHierarchies.All(h => h.HierarchyLabel is null),
            "The generated hierarchies without a 'label' translation do not all correspond to a NULL " +
            $"HierarchyLabel in Access.Hierarchy. Found: {accessHierarchies.Count} of 3, " +
            $"NULL: {accessHierarchies.Count(h => h.HierarchyLabel is null)}.");
    }

    /// <summary>Global census: only 3 concepts are left without a 'label' translation (it used to be 109,552 before the fix).</summary>
    [DataFact]
    public void ConceptTranslation_LabelCount_EqualsConceptCountMinusThree()
    {
        var translationRows = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mConceptTranslation\" WHERE \"Role\" = 'label'");
        var conceptRows = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mConcept\"");

        // 121,571 concepts, 3 without a label => exactly 121,568 'label' translations.
        Assert.True(
            translationRows == conceptRows - 3,
            $"mConceptTranslation with Role='label': {translationRows} rows versus mConcept: {conceptRows} " +
            "(exactly concepts - 3 expected).");
    }

    // ------------------------------------------------------------------
    // A-INT-07: no mConcept without any row that uses it. It protects the taxonomies that have
    // no reference: a dangling concept is an object that "describes nothing".
    // ------------------------------------------------------------------

    [DataFact]
    public void NoConcept_IsUnreferencedByAnyRow()
    {
        // Union of ALL the "owner" ConceptID columns (a row describes a concept), built in
        // memory: with full scans of small/medium tables it is faster and simpler than 15 nested
        // NOT EXISTS without an index on ConceptID.
        string[] ownerTables =
        [
            "mDomain", "mMember", "mDimension", "mHierarchy", "mHierarchyNode",
            "mAxis", "mAxisOrdinate", "mTable", "mTemplateOrTable", "mModule",
            "mTaxonomy", "mReportingFramework", "mRelease", "mOwner", "mLanguage",
        ];

        var referenced = new HashSet<int>();
        foreach (var table in ownerTables)
        {
            var rows = QueryHelpers.Rows(
                _fixture.GeneratedConnection, $"SELECT DISTINCT \"ConceptID\" FROM \"{table}\" WHERE \"ConceptID\" IS NOT NULL", 1);
            foreach (var row in rows)
            {
                referenced.Add(int.Parse(row[0]!));
            }
        }

        var allConceptIds = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"ConceptID\" FROM \"mConcept\"", 1)
            .Select(r => int.Parse(r[0]!))
            .ToList();

        var unreferenced = allConceptIds.Where(id => !referenced.Contains(id)).ToList();

        Assert.True(
            unreferenced.Count == 0,
            $"{unreferenced.Count} of {allConceptIds.Count} concepts with NO row referencing them " +
            $"(A-INT-07 - the most important invariant for taxonomies without a reference). " +
            $"ConceptID examples: {string.Join(",", unreferenced.Take(20))}");
    }

    // ------------------------------------------------------------------
    // mTable.JsonBlob is an EMPTY BLOB, never NULL.
    // ------------------------------------------------------------------

    [DataFact]
    public void MTable_JsonBlob_IsNeverNull_AndIsAlwaysZeroLength()
    {
        var nullCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mTable\" WHERE \"JsonBlob\" IS NULL");
        var zeroLengthCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mTable\" WHERE length(\"JsonBlob\") = 0");
        var totalCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mTable\"");

        Assert.True(nullCount == 0, $"mTable.JsonBlob NULL: {nullCount} rows (0 expected).");
        Assert.True(
            zeroLengthCount == totalCount,
            $"mTable.JsonBlob of length 0: {zeroLengthCount} of {totalCount} rows (ALL expected). " +
            "2,561 expected over --all.");
    }
}
