using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// <c>mTable</c>, <c>mTaxonomyTable</c> and <c>mTemplateOrTable</c> -- the DPM 2.0 template tree.
/// It reuses <see cref="Dpm20SkeletonFixture"/> (collection <c>Dpm2Skeleton</c>): the same
/// <c>--all</c> conversion already loads the complete structure
/// (<see cref="EbaDpm.Converter.Core.Mapping.Dpm20.Dpm20StructureLoader"/> always runs, without a
/// separate flag), so the large DPM 2.0 Access database does not need to be re-read for this.
///
/// The matches against the source are by CODE; what is emitted, and what is checked here, are
/// ROWS. The populations are scoped pins of <see cref="ReleasePins"/> (scope
/// <see cref="ReleasePins.Dpm20Cutoff42"/>): they are outputs of the table closure and of the
/// template tree rules, so they cannot be derived without re-implementing those rules. Every other
/// figure is derived from the generated <c>.db</c> itself (an identity between populations).
///
/// The pair <c>(if 4.2, C_34.02.b)</c> is emitted because a module composition
/// declared <c>C_34.02.b</c> as an ABSTRACT table and the closure that does not resolve abstract
/// tables used to lose it. This is the gap that <see cref="Dpm20AxisAndCellTests"/> used to
/// document. No assertion was ever relaxed, only the new correct value re-measured.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class Dpm20StructureLoaderTests(Dpm20SkeletonFixture fixture)
{
    // ------------------------------------------------------------------
    // 1 - ROW counts: mTable = mTaxonomyTable = BusinessTable rows; mTemplateOrTable = L1 + L2 + BusinessTable
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>mTable</c> has one row per (taxonomy, table) pair. The expected figure is derived from the
    /// Access database (module versions in force, grouped by framework and start release, tables
    /// composed plus the abstract closure), not pinned.
    /// </summary>
    [DataFact]
    public void MTable_RowCount_EqualsThePairsDerivedFromAccess()
    {
        var expected = AccessCensus.TableCodesByTaxonomy(RepoPaths.AccessDpm20DatabasePath, RepoPaths.Cutoff42ReleaseCode)
            .Values.Sum(v => v.Count);
        var actual = CountRows("mTable");
        Assert.True(expected > 0, "The independent query returned no pair: the comparison would be vacuous.");
        Assert.True(
            expected == actual,
            $"mTable rows: expected {expected} (derived from '{RepoPaths.AccessDpm20DatabaseFileName}'), actual {actual}.");
    }

    [DataFact]
    public void MTaxonomyTable_HasOneRowPerMTableRow() => AssertRowsEqualMTable("mTaxonomyTable");

    [DataFact]
    public void MTemplateOrTable_RowCount_IsThePinnedCensus_AndEqualsGroupsPlusBusinessTables()
    {
        var total = CountRows("mTemplateOrTable");
        var level1 = CountTableGroups(level: 1);
        var level2 = CountTableGroups(level: 2);
        var businessTables = ScalarLong("SELECT COUNT(1) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'BusinessTable'");

        var pin = ReleasePins.Dpm20Cutoff42;
        ReleasePins.AssertPinned(pin, "mTemplateOrTable rows", ReleasePins.Dpm20Cutoff42MTemplateOrTableRows, total);
        ReleasePins.AssertPinned(pin, "mTemplateOrTable TableGroup rows, Level 1", ReleasePins.Dpm20Cutoff42TableGroupLevel1Rows, level1);
        ReleasePins.AssertPinned(pin, "mTemplateOrTable TableGroup rows, Level 2", ReleasePins.Dpm20Cutoff42TableGroupLevel2Rows, level2);
        AssertRowsEqualMTable("mTemplateOrTable (BusinessTable rows)", businessTables);

        // Identity: every mTemplateOrTable row is a Level 1 group, a Level 2 group or a BusinessTable.
        Assert.True(
            total == level1 + level2 + businessTables,
            $"mTemplateOrTable rows: expected {level1 + level2 + businessTables} (Level 1 groups {level1} + Level 2 groups {level2} + BusinessTable rows {businessTables}), actual {total} (derived from the generated .db).");
    }

    // ------------------------------------------------------------------
    // 2 - The invariant that looks at no reference: mTaxonomyTable and the BusinessTable rows of
    // mTemplateOrTable are the SAME set of (TaxonomyID, TableID) pairs. The loader already checks it
    // at run time (PairInvariantHolds); this test checks it for real, by querying the written .db
    // rather than trusting the flag the loader itself computed.
    // ------------------------------------------------------------------

    [DataFact]
    public void MTaxonomyTable_And_BusinessTable_AreTheSamePairSet()
    {
        var taxonomyTablePairs = ReadPairs(
            "SELECT \"TaxonomyID\", \"TableID\" FROM \"mTaxonomyTable\"");
        var businessTablePairs = ReadPairs(
            "SELECT \"TaxonomyID\", \"TemplateOrTableID\" FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'BusinessTable'");

        // See the class comment on the pair (if 4.2, C_34.02.b).
        var mTableRows = CountRows("mTable");
        Assert.True(mTableRows == taxonomyTablePairs.Count, $"mTaxonomyTable pairs: expected {mTableRows} (mTable rows), actual {taxonomyTablePairs.Count}.");
        Assert.True(mTableRows == businessTablePairs.Count, $"BusinessTable pairs: expected {mTableRows} (mTable rows), actual {businessTablePairs.Count}.");

        var onlyInTaxonomyTable = taxonomyTablePairs.Except(businessTablePairs).ToList();
        var onlyInBusinessTable = businessTablePairs.Except(taxonomyTablePairs).ToList();

        Assert.True(
            onlyInTaxonomyTable.Count == 0 && onlyInBusinessTable.Count == 0,
            $"The sets are NOT identical: {onlyInTaxonomyTable.Count} pairs only in mTaxonomyTable "
            + $"({string.Join(", ", onlyInTaxonomyTable.Take(10))}), {onlyInBusinessTable.Count} only in "
            + $"mTemplateOrTable/BusinessTable ({string.Join(", ", onlyInBusinessTable.Take(10))}).");
    }

    /// <summary><c>mTaxonomyTable.AnnotatedTableID = TableID</c> in every row, without a single exception.</summary>
    [DataFact]
    public void MTaxonomyTable_AnnotatedTableID_EqualsTableID_InEveryRow()
    {
        AssertRowsEqualMTable("mTaxonomyTable");
        var mismatches = ScalarLong("SELECT COUNT(*) FROM \"mTaxonomyTable\" WHERE \"AnnotatedTableID\" <> \"TableID\"");
        Assert.Equal(0, mismatches);
    }

    /// <summary>
    /// <c>Order</c> is contiguous <c>0..n-1</c>, with no gaps or duplicates, WITHIN EACH GROUP OF
    /// SIBLINGS WITH A REAL PARENT (<c>ParentTemplateOrTableID &lt;&gt; 0</c>). It covers at once the
    /// Level 2 template nodes under their group and the <c>BusinessTable</c> rows under their
    /// template (see the test named below). The groups with <c>Parent = 0</c> (the root and the
    /// orphans) are deliberately NOT included: they follow their own rule, checked separately.
    /// </summary>
    [DataFact]
    public void Order_IsContiguous0ToNMinus1_WithinEachSiblingGroupWithARealParent()
    {
        var rows = ReadTriples(
            "SELECT \"ParentTemplateOrTableID\", \"Order\", \"TemplateOrTableCode\" "
            + "FROM \"mTemplateOrTable\" WHERE \"ParentTemplateOrTableID\" <> 0");

        Assert.True(rows.Count > 0, "There are no rows with a real parent; nothing to check (unexpected).");

        var nonDense = new List<string>();
        foreach (var group in rows.GroupBy(r => r.A))
        {
            var orders = group.Select(r => r.B).OrderBy(o => o).ToList();
            var expected = Enumerable.Range(0, orders.Count).ToList();
            if (!orders.SequenceEqual(expected))
            {
                nonDense.Add($"ParentID={group.Key}: Order=[{string.Join(",", orders)}], expected 0..{orders.Count - 1}");
            }
        }

        Assert.True(
            nonDense.Count == 0,
            $"{nonDense.Count} sibling groups with a real parent and a non-dense Order 0..N-1. Examples: "
            + string.Join(" | ", nonDense.Take(10)));
    }

    // ------------------------------------------------------------------
    // 3 - The tree: three strata, Level is not depth, root/orphans with a literal Parent = 0.
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>Level</c> is NOT the depth of the tree: a <c>BusinessTable</c> is <c>Level</c> 1 and hangs
    /// from a <c>Level</c> 2 node. It checks the invariant in ALL rows with a real parent (all of
    /// them; the pair <c>(if 4.2, C_34.02.b)</c> hangs from a real parent), not just the witness case
    /// -- a test that "fixed" this by expecting the parent to be Level 0 would be wrong.
    /// </summary>
    [DataFact]
    public void BusinessTable_IsLevel1_AndItsRealParentIsLevel2()
    {
        var rows = ReadIntTriples(
            """
            SELECT bt."TemplateOrTableID", bt."Level", parent."Level"
            FROM "mTemplateOrTable" bt
            JOIN "mTemplateOrTable" parent ON parent."TemplateOrTableID" = bt."ParentTemplateOrTableID"
            WHERE bt."TemplateOrTableType" = 'BusinessTable' AND bt."ParentTemplateOrTableID" <> 0
            """);

        // Every BusinessTable row has a real parent (no orphans): as many as mTable rows.
        Assert.True(
            rows.Count == CountRows("mTable"),
            $"BusinessTable rows with a real parent: expected {CountRows("mTable")} (mTable rows, derived from the generated .db), actual {rows.Count}.");

        var wrong = rows.Where(r => r.B != 1 || r.C != 2).ToList();
        Assert.True(
            wrong.Count == 0,
            $"{wrong.Count} BusinessTable rows with a real parent do not satisfy (Level=1, parent.Level=2). "
            + $"Examples (TemplateOrTableID, Level, parent.Level): {string.Join(", ", wrong.Take(10))}");
    }

    /// <summary>
    /// The root carries <c>ParentTemplateOrTableID = 0</c>, NEVER
    /// <c>NULL</c> (this is not universal across the other mappings). It is checked explicitly, in
    /// the populations that could carry 0: the Level 1 groups, the (now absent) orphans and, for
    /// completeness, that the whole column is never NULL in any row.
    /// </summary>
    [DataFact]
    public void Root_And_Orphans_CarryParentEqualTo0_NeverNull()
    {
        Assert.Equal(0, ScalarLong("SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"ParentTemplateOrTableID\" IS NULL"));

        var rootParents = ReadIntColumn(
            "SELECT \"ParentTemplateOrTableID\" FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'TableGroup' AND \"Level\" = 1");
        var level1Groups = CountTableGroups(level: 1);
        Assert.True(
            level1Groups == rootParents.Count,
            $"Level 1 TableGroup rows read: expected {level1Groups} (COUNT(1) of the Level 1 TableGroup rows of the generated mTemplateOrTable), actual {rootParents.Count}.");
        Assert.All(rootParents, p => Assert.Equal(0, p));

        // Since issue 66 the template tree is built for EVERY taxonomy, so no BusinessTable row is an
        // orphan: every one of them hangs from a real parent (no more Parent = 0 rows besides the roots).
        var orphanParents = ReadIntColumn(
            "SELECT \"ParentTemplateOrTableID\" FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'BusinessTable' AND \"ParentTemplateOrTableID\" = 0");
        Assert.Empty(orphanParents);

        // Confirms with type literalness: the column is an integer, and the value read is the
        // integer 0 (not an empty string or another DBNull in disguise) -- through a typed read.
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText =
            "SELECT \"ParentTemplateOrTableID\" FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'TableGroup' AND \"Level\" = 1 LIMIT 1";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.False(reader.IsDBNull(0), "ParentTemplateOrTableID of the root is DBNull; it must be the integer 0.");
        Assert.Equal(0, reader.GetInt32(0));
    }

    /// <summary>The <c>TableGroup</c> nodes (Level 1 and Level 2) carry <c>eba_tg</c> in front; the <c>BusinessTable</c> rows, none.</summary>
    [DataFact]
    public void TableGroup_CarriesEbaTgPrefix_BusinessTable_Never()
    {
        var allGroups = ScalarLong("SELECT COUNT(1) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'TableGroup'");
        var level1AndLevel2 = CountTableGroups(level: 1) + CountTableGroups(level: 2);
        Assert.True(
            allGroups == level1AndLevel2,
            $"TableGroup rows: expected {level1AndLevel2} (Level 1 + Level 2 TableGroup rows of the generated mTemplateOrTable), actual {allGroups}.");

        var groupsWithoutPrefix = ScalarLong(
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'TableGroup' AND \"TemplateOrTableCode\" NOT LIKE 'eba_tg%'");
        Assert.Equal(0, groupsWithoutPrefix);

        var tablesWithPrefix = ScalarLong(
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'BusinessTable' AND \"TemplateOrTableCode\" LIKE 'eba_tg%'");
        Assert.Equal(0, tablesWithPrefix);
    }

    /// <summary>
    /// Witness case: the template of a table is its <c>AbstractTableID</c>, or the table itself.
    /// <c>C_02.00.a</c> and <c>C_02.00.b</c> hang from <c>eba_tgC_02.00</c>, which is the abstract
    /// <c>C_02.00</c>. Measured in the real .db: TemplateOrTableID 59 (C_02.00.a) and 178
    /// (C_02.00.b), both with ParentTemplateOrTableID = 884, which is eba_tgC_02.00.
    /// </summary>
    [DataFact]
    public void WitnessCase_C0200a_And_C0200b_HangFromEbaTgC0200()
    {
        var parentCodeOfA = ScalarString(
            "SELECT parent.\"TemplateOrTableCode\" FROM \"mTemplateOrTable\" bt "
            + "JOIN \"mTemplateOrTable\" parent ON parent.\"TemplateOrTableID\" = bt.\"ParentTemplateOrTableID\" "
            + "WHERE bt.\"TemplateOrTableCode\" = 'C_02.00.a' AND bt.\"TemplateOrTableType\" = 'BusinessTable' AND bt.\"ParentTemplateOrTableID\" <> 0");
        var parentCodeOfB = ScalarString(
            "SELECT parent.\"TemplateOrTableCode\" FROM \"mTemplateOrTable\" bt "
            + "JOIN \"mTemplateOrTable\" parent ON parent.\"TemplateOrTableID\" = bt.\"ParentTemplateOrTableID\" "
            + "WHERE bt.\"TemplateOrTableCode\" = 'C_02.00.b' AND bt.\"TemplateOrTableType\" = 'BusinessTable' AND bt.\"ParentTemplateOrTableID\" <> 0");

        Assert.Equal("eba_tgC_02.00", parentCodeOfA);
        Assert.Equal("eba_tgC_02.00", parentCodeOfB);
    }

    /// <summary>
    /// <c>Order</c> follows NATURAL order, never alphabetical: <c>C_09.04</c> &lt; <c>C_10.00</c>
    /// &lt; <c>C_11.00</c> within the SAME parent. Alphabetically "C_10.00" would come before
    /// "C_09.04" (the character '1' is less than '9'); the correct natural order puts them the other
    /// way round. Measured in the real .db: the three are template nodes
    /// (eba_tgC_09.04/eba_tgC_10.00/eba_tgC_11.00), same parent (TemplateOrTableID 899), with Order
    /// 11/12/15.
    /// </summary>
    [DataFact]
    public void Order_IsNatural_NotAlphabetical_C0904BeforeC1000BeforeC1100()
    {
        var rows = ReadCodeParentOrder(["eba_tgC_09.04", "eba_tgC_10.00", "eba_tgC_11.00"]);

        Assert.Equal(3, rows.Count);
        var parents = rows.Select(r => r.ParentId).Distinct().ToList();
        Assert.True(parents.Count == 1, $"The three witness codes do not share a parent: {string.Join(",", parents)}.");

        var orderByCode = rows.ToDictionary(r => r.Code, r => r.Order, StringComparer.Ordinal);
        Assert.True(orderByCode["eba_tgC_09.04"] < orderByCode["eba_tgC_10.00"]);
        Assert.True(orderByCode["eba_tgC_10.00"] < orderByCode["eba_tgC_11.00"]);
    }

    /// <summary>
    /// The template tree is built for EVERY taxonomy (issue 66): every taxonomy that has
    /// <c>BusinessTable</c> rows has <c>TableGroup</c> nodes too, every <c>BusinessTable</c> row has a
    /// real parent, and the taxonomies that carry <c>BusinessTable</c> rows are exactly those of
    /// <c>mTaxonomyTable</c>.
    /// </summary>
    [DataFact]
    public void TheTreeExistsInEveryTaxonomyThatHasTables_AndNoBusinessTableIsAnOrphan()
    {
        var taxonomyIdsWithTables = ReadIntColumn(
            "SELECT DISTINCT \"TaxonomyID\" FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'BusinessTable'");
        var taxonomyIdsWithGroupNodes = ReadIntColumn(
            "SELECT DISTINCT \"TaxonomyID\" FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'TableGroup'").ToHashSet();
        var taxonomyIdsInTaxonomyTable = ReadIntColumn("SELECT DISTINCT \"TaxonomyID\" FROM \"mTaxonomyTable\"");

        Assert.NotEmpty(taxonomyIdsWithTables);
        Assert.Equal(taxonomyIdsInTaxonomyTable.OrderBy(i => i), taxonomyIdsWithTables.OrderBy(i => i));

        var withoutGroups = taxonomyIdsWithTables.Where(id => !taxonomyIdsWithGroupNodes.Contains(id)).ToList();
        Assert.True(withoutGroups.Count == 0, $"Taxonomies with BusinessTable rows but without TableGroup nodes: {string.Join(",", withoutGroups)}");

        var orphans = ScalarLong(
            "SELECT COUNT(1) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'BusinessTable' AND \"ParentTemplateOrTableID\" = 0");
        Assert.True(orphans == 0, $"{orphans} BusinessTable rows are orphans (Parent = 0).");
    }

    // ------------------------------------------------------------------
    // 4 - mTable: measured columns
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>XbrlFilingIndicatorCode</c> = the code of the table's TEMPLATE, WITHOUT the
    /// <c>eba_tg</c> prefix. It checks, in every row, that
    /// <c>'eba_tg' + XbrlFilingIndicatorCode</c> exists among the codes of the <c>Level</c> 2 nodes
    /// (the distinct templates) -- the check is GLOBAL, not per taxonomy: the template is a
    /// per-table concept, not per (table, taxonomy) pair, and its tree nodes are only materialized
    /// where applicable.
    /// </summary>
    [DataFact]
    public void MTable_XbrlFilingIndicatorCode_IsTheTemplateCodeWithoutPrefix()
    {
        var level2Codes = ReadStringColumn(
                "SELECT DISTINCT \"TemplateOrTableCode\" FROM \"mTemplateOrTable\" WHERE \"Level\" = 2 AND \"TemplateOrTableType\" = 'TableGroup'")
            .Where(c => c is not null)
            .Select(c => c!)
            .ToHashSet(StringComparer.Ordinal);

        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Cutoff42, "distinct Level 2 template codes", ReleasePins.Dpm20Cutoff42DistinctLevel2TemplateCodes, level2Codes.Count);

        var indicatorRows = ReadPairsStringString(
            "SELECT \"TableCode\", \"XbrlFilingIndicatorCode\" FROM \"mTable\"");

        var mTableRows = CountRows("mTable");
        Assert.True(mTableRows == indicatorRows.Count, $"mTable rows read: expected {mTableRows} (COUNT(1) of mTable), actual {indicatorRows.Count}.");

        var missing = indicatorRows
            .Where(r => r.B is null || !level2Codes.Contains("eba_tg" + r.B))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} mTable rows whose 'eba_tg' + XbrlFilingIndicatorCode does not exist among "
            + $"the Level 2 nodes. Examples: {string.Join(", ", missing.Take(10).Select(m => $"{m.A}->{m.B}"))}");
    }

    /// <summary><c>TableLabel</c> = <c>&lt;code&gt;:&lt;Name&gt;</c> -- the target prepends the code and a colon, in every row.</summary>
    [DataFact]
    public void MTable_TableLabel_StartsWithCodeColon_InEveryRow()
    {
        var rows = ReadPairsStringString("SELECT \"TableCode\", \"TableLabel\" FROM \"mTable\"");
        var mTableRows = CountRows("mTable");
        Assert.True(mTableRows == rows.Count, $"mTable rows read: expected {mTableRows} (COUNT(1) of mTable), actual {rows.Count}.");

        var mismatches = rows.Where(r => r.B is null || !r.B.StartsWith(r.A + ":", StringComparison.Ordinal)).ToList();
        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count} rows whose TableLabel does not start with '<code>:'. Examples: "
            + string.Join(", ", mismatches.Take(10).Select(m => $"{m.A}->{m.B}")));
    }

    /// <summary>
    /// The name is NOT trimmed: several labels keep a trailing space exactly as in the source, and
    /// that is correct -- <c>Trim</c> would fix some and break others. This test does not compare
    /// against the reference (no comparison is relaxed); it compares AGAINST THE SOURCE ITSELF, row
    /// by row, reading <c>TableVersion.Name</c> directly from the Access database with
    /// <see cref="Dpm20AccessReader"/>, for a batch of tables whose source name ends in a space --
    /// showing that the space survives without anyone trimming it.
    /// </summary>
    [DataFact]
    public void MTable_TableLabel_DoesNotTrimTheName_AgainstTheSourceRowByRow()
    {
        RepoPaths.EnsureAccessDpm20DatabaseExists();

        using var reader = new Dpm20AccessReader(RepoPaths.AccessDpm20DatabasePath, cutoffReleaseCode: RepoPaths.Cutoff42ReleaseCode);
        reader.Open();
        var tableVersionsWithTrailingSpaceName = reader.ReadTableVersions()
            .Where(tv => tv.Name is not null && tv.Name.EndsWith(' '))
            .ToList();

        Assert.True(
            tableVersionsWithTrailingSpaceName.Count > 0,
            "There is no current TableVersion whose Name ends in a space in the source; the witness "
            + "case (9 labels with a trailing space) cannot be checked this way.");

        // Note: mTable has more ROWS than distinct CODES (a code can appear in more than one
        // table-taxonomy pair); the label is a function of the code, not of the pair, so grouping
        // and taking the first one is correct -- no information is lost in the comparison.
        var generatedLabelsByCode = ReadPairsStringString("SELECT \"TableCode\", \"TableLabel\" FROM \"mTable\"")
            .Where(r => r.B is not null)
            .GroupBy(r => r.A, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().B!, StringComparer.Ordinal);

        var checkedCount = 0;
        var mismatches = new List<string>();
        foreach (var tv in tableVersionsWithTrailingSpaceName)
        {
            if (!generatedLabelsByCode.TryGetValue(tv.Code, out var generatedLabel))
            {
                continue; // e.g. abstract, or excluded because of DORA: it emits no mTable row.
            }

            checkedCount++;
            var expectedLabel = tv.Code + ":" + tv.Name;
            if (!string.Equals(generatedLabel, expectedLabel, StringComparison.Ordinal))
            {
                mismatches.Add($"{tv.Code}: expected '{expectedLabel}' (NOT trimmed), generated '{generatedLabel}'");
            }
        }

        Assert.True(checkedCount > 0, "None of the TableVersion rows with a Name ending in a space produces an mTable row; nothing to check.");
        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count} of {checkedCount} labels with a trailing space in the source were NOT "
            + $"reproduced as they are (is it being trimmed?): {string.Join(" | ", mismatches.Take(10))}");
    }

    /// <summary><c>JsonBlob</c> = empty BLOB (not NULL, not a string) in every row; <c>FromDate</c>, <c>ToDate</c>, <c>XbrlTableCode</c>, <c>YDimVal</c>, <c>ZDimVal</c> NULL.</summary>
    [DataFact]
    public void MTable_JsonBlobEmpty_And_DateAndDimensionFieldsNull()
    {
        Assert.True(CountRows("mTable") > 0, "mTable is empty: the column checks below would pass vacuously.");

        Assert.Equal(0, ScalarLong("SELECT COUNT(*) FROM \"mTable\" WHERE \"JsonBlob\" IS NULL"));
        Assert.Equal(0, ScalarLong("SELECT COUNT(*) FROM \"mTable\" WHERE length(\"JsonBlob\") <> 0"));

        Assert.Equal(
            0,
            ScalarLong(
                "SELECT COUNT(*) FROM \"mTable\" WHERE \"FromDate\" IS NOT NULL OR \"ToDate\" IS NOT NULL "
                + "OR \"XbrlTableCode\" IS NOT NULL OR \"YDimVal\" IS NOT NULL OR \"ZDimVal\" IS NOT NULL"));
    }

    /// <summary><c>mTaxonomyTable.IsSimplyReuse</c> and <c>IsTableSource</c>: constant 1 in every row.</summary>
    [DataFact]
    public void MTaxonomyTable_IsSimplyReuse_And_IsTableSource_AreConstant1_InEveryRow()
    {
        AssertRowsEqualMTable("mTaxonomyTable");
        Assert.Equal(0, ScalarLong("SELECT COUNT(*) FROM \"mTaxonomyTable\" WHERE \"IsSimplyReuse\" <> 1"));
        Assert.Equal(0, ScalarLong("SELECT COUNT(*) FROM \"mTaxonomyTable\" WHERE \"IsTableSource\" <> 1"));
    }

    // ------------------------------------------------------------------
    // 5 - Against the reference, by CONTAINMENT, never by equality of counts.
    // ------------------------------------------------------------------

    /// <summary>
    /// The distinct <c>mTable</c> codes against <c>EBA_4.2_Hotfix.db</c>: PERFECT containment, none only in
    /// the reference. CRITICAL layer: coverage -- what is generated must contain the reference.
    /// </summary>
    [DataFact]
    public void MTable_TableCode_Contains_TheCodesOfTheReference_PerfectContainment()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var generatedCodes = ReadStringColumn("SELECT DISTINCT \"TableCode\" FROM \"mTable\"")
            .Where(c => c is not null).Select(c => c!).ToHashSet(StringComparer.Ordinal);
        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Cutoff42, "distinct mTable codes", ReleasePins.Dpm20Cutoff42EmittedTableCodes, generatedCodes.Count);

        using var referenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);
        using var command = referenceConnection.CreateCommand();
        command.CommandText = "SELECT DISTINCT \"TableCode\" FROM \"mTable\"";
        var referenceCodes = new HashSet<string>(StringComparer.Ordinal);
        using (var readerCmd = command.ExecuteReader())
        {
            while (readerCmd.Read())
            {
                referenceCodes.Add(readerCmd.GetString(0));
            }
        }

        ReleasePins.AssertPinned(
            ReleasePins.Reference42, "reference distinct mTable codes", ReleasePins.Reference42MTableCodes, referenceCodes.Count);

        var onlyInReference = referenceCodes.Except(generatedCodes).ToList();
        Assert.True(
            onlyInReference.Count == 0,
            $"{onlyInReference.Count} mTable codes only in the reference (should be 0): "
            + string.Join(", ", onlyInReference.Take(20)));
    }

    // ------------------------------------------------------------------
    // 6 - Named cases
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>S_04.00</c> used to appear also as an orphan in <c>pay 4.1</c>. Since issue 66 no PAY
    /// module version starts in 4.1, so <c>pay 4.1</c> does not exist: the table is in <c>pay 4.2</c>
    /// only, with a real parent. Nominal test of the case, by business key.
    /// </summary>
    [DataFact]
    public void NamedCase_S0400_OnlyInPay42_WithARealParent()
    {
        var taxonomyCodeById = ReadTaxonomyCodes();

        var rows = ReadPairsIntWithCode(
            "SELECT tt.\"TaxonomyID\", bt.\"ParentTemplateOrTableID\" "
            + "FROM \"mTaxonomyTable\" tt "
            + "JOIN \"mTable\" mt ON mt.\"TableID\" = tt.\"TableID\" "
            + "JOIN \"mTemplateOrTable\" bt ON bt.\"TemplateOrTableID\" = tt.\"TableID\" AND bt.\"TemplateOrTableType\" = 'BusinessTable' "
            + "WHERE mt.\"TableCode\" = 'S_04.00'");

        Assert.Single(rows);
        Assert.Equal("pay 4.2", taxonomyCodeById.GetValueOrDefault(rows[0].A));
        Assert.NotEqual(0, rows[0].B); // real parent
        Assert.DoesNotContain("pay 4.1", taxonomyCodeById.Values);
    }

    /// <summary>
    /// <c>C_34.02.b</c> DOES appear in <c>if 4.2</c>. It used to be a documented gap (the pair
    /// <c>(C_34.02.b, if 4.2)</c> was missing). The module composition declares <c>C_34.02.b</c> as
    /// an ABSTRACT table of <c>if 4.2</c>, and the abstract-closure rule adds its current concrete
    /// children when a composition references an abstract table -- closing exactly that gap. The
    /// discarded hypothesis (propagating the taxonomies of the whole GROUP, which overproduced 26
    /// pairs) remains discarded: the closure rule resolves the abstract table declared in the
    /// composition, not the group. It is stated as a PROPERTY by business key (never by ID) with a
    /// POSITIVE CONTROL: if <c>C_34.02.a</c> -- which was already present before -- stopped being
    /// found, the query would be wrongly written and the rest of the test would prove nothing.
    /// </summary>
    [DataFact]
    public void NamedCase_C3402b_AppearsInCorep42_AndInIf42()
    {
        var taxonomyCodeById = ReadTaxonomyCodes();

        List<string> TaxonomyCodesFor(string tableCode) => ReadIntColumn(
                "SELECT tt.\"TaxonomyID\" FROM \"mTaxonomyTable\" tt "
                + "JOIN \"mTable\" mt ON mt.\"TableID\" = tt.\"TableID\" WHERE mt.\"TableCode\" = '" + tableCode + "'")
            .Select(id => taxonomyCodeById.GetValueOrDefault(id) ?? "?")
            .ToList();

        // Positive control: C_34.02.a was already present BEFORE the abstract-closure rule -- if the
        // query does not find it, it is wrongly written and the result below about C_34.02.b proves
        // nothing.
        var codesForA = TaxonomyCodesFor("C_34.02.a");
        Assert.Contains("if 4.2", codesForA);
        Assert.Contains("corep 4.2", codesForA);

        var codesForB = TaxonomyCodesFor("C_34.02.b");
        Assert.Contains("corep 4.2", codesForB); // was already there, untouched by the rule
        Assert.Contains("if 4.2", codesForB); // the pair that the abstract-closure rule adds
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private long CountRows(string table)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private long CountTableGroups(int level)
        => ScalarLong(
            $"SELECT COUNT(1) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'TableGroup' AND \"Level\" = {level}");

    private void AssertRowsEqualMTable(string table) => AssertRowsEqualMTable(table, CountRows(table));

    /// <summary>Identity: the rows of <paramref name="table"/> are one per <c>mTable</c> row.</summary>
    private void AssertRowsEqualMTable(string table, long actual)
    {
        var mTableRows = CountRows("mTable");
        Assert.True(
            mTableRows == actual,
            $"{table} rows: expected {mTableRows} (mTable rows, derived from the generated .db), actual {actual}.");
    }

    private long ScalarLong(string sql)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private string? ScalarString(string sql)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private List<int> ReadIntColumn(string sql)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var values = new List<int>();
        while (reader.Read())
        {
            values.Add(reader.GetInt32(0));
        }

        return values;
    }

    private List<string?> ReadStringColumn(string sql)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var values = new List<string?>();
        while (reader.Read())
        {
            values.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        return values;
    }

    private HashSet<(int A, int B)> ReadPairs(string sql)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var values = new HashSet<(int, int)>();
        while (reader.Read())
        {
            values.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }

        return values;
    }

    private List<(int A, int B, int C)> ReadIntTriples(string sql)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var values = new List<(int, int, int)>();
        while (reader.Read())
        {
            values.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
        }

        return values;
    }

    private List<(string A, int B, string C)> ReadTriples(string sql)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var values = new List<(string, int, string)>();
        while (reader.Read())
        {
            values.Add((reader.GetValue(0)?.ToString() ?? string.Empty, reader.GetInt32(1), reader.GetValue(2)?.ToString() ?? string.Empty));
        }

        return values;
    }

    private List<(string A, string? B)> ReadPairsStringString(string sql)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var values = new List<(string, string?)>();
        while (reader.Read())
        {
            values.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return values;
    }

    private List<(int A, int B)> ReadPairsIntWithCode(string sql)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var values = new List<(int, int)>();
        while (reader.Read())
        {
            values.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }

        return values;
    }

    private List<(string Code, int ParentId, int Order)> ReadCodeParentOrder(IReadOnlyCollection<string> codes)
    {
        var placeholders = string.Join(",", codes.Select((_, i) => $"$c{i}"));
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText =
            $"SELECT \"TemplateOrTableCode\", \"ParentTemplateOrTableID\", \"Order\" "
            + $"FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableCode\" IN ({placeholders})";

        var i = 0;
        foreach (var code in codes)
        {
            command.Parameters.AddWithValue($"$c{i}", code);
            i++;
        }

        using var reader = command.ExecuteReader();
        var values = new List<(string, int, int)>();
        while (reader.Read())
        {
            values.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
        }

        return values;
    }

    private Dictionary<int, string> ReadTaxonomyCodes()
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = "SELECT \"TaxonomyID\", \"TaxonomyCode\" FROM \"mTaxonomy\"";
        using var reader = command.ExecuteReader();

        var result = new Dictionary<int, string>();
        while (reader.Read())
        {
            result[reader.GetInt32(0)] = reader.GetString(1);
        }

        return result;
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
