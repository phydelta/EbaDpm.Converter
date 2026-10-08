using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dictionary;

/// <summary>
/// Exact rules of the template/table loading (see the type comment of <c>TemplateOrTableLoader</c>):
/// the <c>TableLabel</c> rule, the constants of <c>mTaxonomyTable</c>, and the per-level 0-based
/// <c>Order</c> sorted by code. Critical layer unless stated otherwise.
/// </summary>
[Collection("Dictionary")]
public sealed class TemplateOrTableRulesTests
{
    private readonly DictionaryFixture _fixture;

    public TemplateOrTableRulesTests(DictionaryFixture fixture)
    {
        _fixture = fixture;
    }

    // ------------------------------------------------------------------
    // TableLabel = XbrlTableCode + ":" + TableVersion.TableVersionLabel -- row by row against the
    // Access, without sampling (same rigour standard as for the XBRL codes).
    // ------------------------------------------------------------------

    private Dictionary<string, string?> BuildExpectedLabelsByXbrlTableCode()
    {
        var corep32 = _fixture.SelectedTaxonomies.Single(t => t.TaxonomyCode == DictionaryFixture.TaxonomyCode);

        var tableVIds = _fixture.AccessReader
            .Query($"SELECT [TableVID] FROM [TaxonomyTableVersion] WHERE [TaxonomyID] = {corep32.TaxonomyId}")
            .Select(r => Convert.ToInt32(r.GetValue(0)))
            .Distinct()
            .ToList();

        var tableVersions = _fixture.AccessReader.ReadTableVersionsByIds(tableVIds).ToList();

        var byCode = new Dictionary<string, string?>(StringComparer.Ordinal);
        var duplicateCodes = new List<string>();

        foreach (var tv in tableVersions)
        {
            var code = tv.XbrlTableCode
                ?? throw new InvalidOperationException($"TableVersion without XbrlTableCode (TableVID={tv.TableVId}) in COREP 3.2.");
            var expectedLabel = code + ":" + tv.TableVersionLabel;

            if (!byCode.TryAdd(code, expectedLabel) && byCode[code] != expectedLabel)
            {
                duplicateCodes.Add(code);
            }
        }

        Assert.True(
            duplicateCodes.Count == 0,
            "XbrlTableCode is not a unique key within COREP 3.2 (several TableVersion rows with the " +
            $"same code but a different TableVersionLabel): {string.Join(", ", duplicateCodes)}. " +
            "The business-key comparison of this test assumes uniqueness; review if this appears.");

        return byCode;
    }

    [DataFact]
    public void MTable_TableLabel_MatchesRule_RowByRow_WithoutSampling()
    {
        var expectedByCode = BuildExpectedLabelsByXbrlTableCode();

        var generatedRows = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"TableCode\", \"TableLabel\" FROM \"mTable\"", 2);

        Assert.True(
            generatedRows.Count == expectedByCode.Count,
            $"mTable has {generatedRows.Count} generated rows versus {expectedByCode.Count} " +
            "distinct TableVersion (by XbrlTableCode) in Access.TaxonomyTableVersion of COREP 3.2.");

        var mismatches = new List<string>();
        foreach (var row in generatedRows)
        {
            var tableCode = row[0]!;
            var generatedLabel = row[1];

            if (!expectedByCode.TryGetValue(tableCode, out var expectedLabel))
            {
                mismatches.Add($"TableCode='{tableCode}': does not exist in Access.TaxonomyTableVersion of COREP 3.2 (unexpected)");
                continue;
            }

            if (!string.Equals(generatedLabel, expectedLabel, StringComparison.Ordinal))
            {
                mismatches.Add($"TableCode='{tableCode}': generated='{generatedLabel}' vs expected='{expectedLabel}'");
            }
        }

        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count} of {generatedRows.Count} mTable.TableLabel rows do not follow the " +
            $"rule XbrlTableCode + \":\" + TableVersionLabel. Examples: {string.Join("; ", mismatches.Take(20))}");
    }

    [DataFact]
    public void BusinessTable_TemplateOrTableLabel_MatchesSameRule_RowByRow_WithoutSampling()
    {
        var expectedByCode = BuildExpectedLabelsByXbrlTableCode();

        var generatedRows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT \"TemplateOrTableCode\", \"TemplateOrTableLabel\" FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'BusinessTable'",
            2);

        Assert.True(
            generatedRows.Count == expectedByCode.Count,
            $"mTemplateOrTable(BusinessTable) has {generatedRows.Count} rows versus " +
            $"{expectedByCode.Count} distinct TableVersion in Access.TaxonomyTableVersion of COREP 3.2.");

        var mismatches = new List<string>();
        foreach (var row in generatedRows)
        {
            var code = row[0]!;
            var generatedLabel = row[1];

            if (!expectedByCode.TryGetValue(code, out var expectedLabel))
            {
                mismatches.Add($"TemplateOrTableCode='{code}': does not exist in Access.TaxonomyTableVersion of COREP 3.2 (unexpected)");
                continue;
            }

            if (!string.Equals(generatedLabel, expectedLabel, StringComparison.Ordinal))
            {
                mismatches.Add($"TemplateOrTableCode='{code}': generated='{generatedLabel}' vs expected='{expectedLabel}'");
            }
        }

        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count} of {generatedRows.Count} mTemplateOrTable(BusinessTable)." +
            $"TemplateOrTableLabel rows do not follow the rule XbrlTableCode + \":\" + TableVersionLabel. " +
            $"Examples: {string.Join("; ", mismatches.Take(20))}");
    }

    // ------------------------------------------------------------------
    // mTaxonomyTable: IsSimplyReuse / IsTableSource constant true
    // ------------------------------------------------------------------

    [DataFact]
    public void TaxonomyTable_IsSimplyReuse_And_IsTableSource_AreConstantTrue()
    {
        var rows = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT DISTINCT \"IsSimplyReuse\", \"IsTableSource\" FROM \"mTaxonomyTable\"", 2);

        Assert.True(rows.Count == 1, $"mTaxonomyTable.(IsSimplyReuse, IsTableSource) is not constant: {rows.Count} distinct combinations.");

        var (isSimplyReuse, isTableSource) = (rows[0][0], rows[0][1]);
        Assert.True(isSimplyReuse is "1" or "True" or "true", $"IsSimplyReuse should be true (1); value: '{isSimplyReuse}'.");
        Assert.True(isTableSource is "1" or "True" or "true", $"IsTableSource should be true (1); value: '{isTableSource}'.");
    }

    /// <summary>
    /// Evidence measured directly in the three references, not in the generated output: it
    /// supports why the constant <c>true</c> is chosen even though 3.2 uses <c>0</c>. If any
    /// reference changed these values, it is a signal to escalate (the constant choice might need
    /// to be reverted), not something the test should absorb silently.
    /// </summary>
    [DataFact]
    public void ReferenceInvariant_IsSimplyReuse_Is0In32_And1In40And42()
    {
        AssertDistinctBooleanColumn(_fixture.Reference32Connection, "EBA_3.2_phase_1.db", "IsSimplyReuse", expectedValue: false);
        AssertDistinctBooleanColumn(_fixture.Reference40Connection, "EBA_4.0_ERRATA_5.db", "IsSimplyReuse", expectedValue: true);
        AssertDistinctBooleanColumn(_fixture.Reference42Connection, "EBA_4.2_Hotfix.db", "IsSimplyReuse", expectedValue: true);
    }

    [DataFact]
    public void ReferenceInvariant_IsTableSource_DoesNotExistIn32_And1In40And42()
    {
        var columns32 = QueryHelpers.Rows(_fixture.Reference32Connection, "PRAGMA table_info('mTaxonomyTable')", 6);
        Assert.False(
            columns32.Any(c => string.Equals(c[1], "IsTableSource", StringComparison.OrdinalIgnoreCase)),
            "EBA_3.2_phase_1.db has an IsTableSource column in mTaxonomyTable; this contradicts the finding that supports the constant choice (review).");

        AssertDistinctBooleanColumn(_fixture.Reference40Connection, "EBA_4.0_ERRATA_5.db", "IsTableSource", expectedValue: true);
        AssertDistinctBooleanColumn(_fixture.Reference42Connection, "EBA_4.2_Hotfix.db", "IsTableSource", expectedValue: true);
    }

    private static void AssertDistinctBooleanColumn(SqliteConnection connection, string dbLabel, string column, bool expectedValue)
    {
        var rows = QueryHelpers.Rows(connection, $"SELECT DISTINCT \"{column}\" FROM \"mTaxonomyTable\"", 1);
        Assert.True(rows.Count == 1, $"{dbLabel}.mTaxonomyTable.{column} is not constant: {rows.Count} distinct values.");

        var expectedText = expectedValue ? "1" : "0";
        Assert.True(
            rows[0][0] == expectedText,
            $"{dbLabel}.mTaxonomyTable.{column} = '{rows[0][0]}', '{expectedText}' was expected.");
    }

    // ------------------------------------------------------------------
    // Order: per level, 0-based, relative order by code (not by label)
    // ------------------------------------------------------------------

    [DataFact]
    public void Level1Nodes_OrderIsConstantZero()
    {
        var nonZero = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'TableGroup' AND \"Level\" = 1 AND \"Order\" <> 0");

        Assert.True(nonZero == 0, $"{nonZero} L1 (TableGroup) nodes with an Order other than 0.");
    }

    [DataFact]
    public void Level2Nodes_OrderIsDenseZeroBased_AndMatchesAlphabeticalCodeOrder_WithinEachParent()
    {
        AssertDenseAlphabeticalOrderWithinParent("TableGroup", level: 2);
    }

    [DataFact]
    public void BusinessTableNodes_OrderIsDenseZeroBased_AndMatchesAlphabeticalCodeOrder_WithinEachParent()
    {
        AssertDenseAlphabeticalOrderWithinParent("BusinessTable", level: 1);
    }

    /// <summary>
    /// For each group of siblings (same <c>ParentTemplateOrTableID</c>, same type/level), checks
    /// two things separately: (a) <c>Order</c> is a dense sequence 0..N-1 (no gaps nor repeats)
    /// and (b) the RELATIVE ORDER it imposes matches the alphabetical (ordinal) order of
    /// <c>TemplateOrTableCode</c> -- <c>Order</c> is never compared by absolute value against any
    /// reference, only internally against the code itself, which is the measured rule.
    /// </summary>
    private void AssertDenseAlphabeticalOrderWithinParent(string type, int level)
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            $"""
            SELECT "ParentTemplateOrTableID", "Order", "TemplateOrTableCode"
            FROM "mTemplateOrTable"
            WHERE "TemplateOrTableType" = '{type}' AND "Level" = {level}
            """,
            3);

        Assert.True(rows.Count > 0, $"There are no rows of type '{type}' and Level={level} in mTemplateOrTable; nothing to check (unexpected).");

        var byParent = rows
            .Select(r => (ParentId: r[0]!, Order: int.Parse(r[1]!), Code: r[2]!))
            .GroupBy(r => r.ParentId);

        var denseMismatches = new List<string>();
        var alphabeticalMismatches = new List<string>();

        foreach (var group in byParent)
        {
            var siblings = group.ToList();
            var expectedOrders = Enumerable.Range(0, siblings.Count).ToHashSet();
            var actualOrders = siblings.Select(s => s.Order).ToHashSet();

            if (!actualOrders.SetEquals(expectedOrders))
            {
                denseMismatches.Add($"ParentID={group.Key}: observed Order=[{string.Join(",", siblings.Select(s => s.Order).OrderBy(o => o))}], expected 0..{siblings.Count - 1}");
                continue;
            }

            var byOrder = siblings.OrderBy(s => s.Order).Select(s => s.Code).ToList();
            var byCodeAlphabetical = siblings.OrderBy(s => s.Code, StringComparer.Ordinal).Select(s => s.Code).ToList();

            if (!byOrder.SequenceEqual(byCodeAlphabetical, StringComparer.Ordinal))
            {
                alphabeticalMismatches.Add(
                    $"ParentID={group.Key}: by Order=[{string.Join(",", byOrder)}] vs alphabetical=[{string.Join(",", byCodeAlphabetical)}]");
            }
        }

        Assert.True(
            denseMismatches.Count == 0,
            $"{denseMismatches.Count} sibling groups '{type}' L{level} with a non-dense Order 0..N-1. Examples: " +
            string.Join(" | ", denseMismatches.Take(10)));

        Assert.True(
            alphabeticalMismatches.Count == 0,
            $"{alphabeticalMismatches.Count} sibling groups '{type}' L{level} whose Order does not follow the " +
            $"alphabetical (ordinal) order of the code. Examples: {string.Join(" | ", alphabeticalMismatches.Take(10))}");
    }
}
