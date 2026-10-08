using EbaDpm.Converter.Tests.Dictionary;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Verification of <c>mTableCell.BusinessCode</c>, <c>DatapointSignature</c> and <c>DPS</c> for the
/// DPM 2.0 source. It reuses <see cref="Dpm20SkeletonFixture"/> (collection <c>Dpm2Skeleton</c>):
/// the same <c>--all</c> conversion always runs <c>Dpm20AxisAndCellLoader.Load</c>, so the large
/// Access database does not need to be re-read.
///
/// The overall <c>DPS</c> match against the reference (about 90%) is not pursued here: some
/// unrelated causes remain open (the release suffix of MET, and the closure versus the minimization
/// of mOrdinateCategorisation); the complete comparison against the reference, with the measured
/// threshold, lives in <see cref="Dpm20OpenAxisValueRestrictionTests"/>. What is fixed HERE are the
/// ALGORITHM INVARIANTS, which do not depend on the quality of the inputs, plus ONE comparison
/// against the reference with a threshold (never equality): the <c>BusinessCode</c>, which is
/// (case,cell)-derivable except for the secondary ordering (about 1,183 of 144,463, chosen, not
/// derived).
///
/// <c>mTableCell</c> has 163,218 rows (including the table <c>C_34.02.b/if 4.2</c>, which adds 440
/// rows); the counts in this file were measured against the real .db. That table has its THREE axes
/// closed (X/Y/Z, none open), so it contributes no rows to the open-axis-with-restriction bracket
/// pattern: the count of Invariant 2 (28,065) does not move.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class Dpm20SignatureTests(Dpm20SkeletonFixture fixture)
{
    // ------------------------------------------------------------------
    // Invariant 1 - Only NON-shaded cells carry a signature (DPS/DatapointSignature).
    // 0 exceptions in BOTH directions: no shaded cell with a signature, no non-shaded cell without one.
    // ------------------------------------------------------------------

    [DataFact]
    public void OnlyNonShadedCells_CarryASignature_0ExceptionsInBothDirections()
    {
        var total = ScalarLong("SELECT COUNT(*) FROM \"mTableCell\"");
        Assert.Equal(163218, total);

        var shadedWithSignature = ScalarLong(
            "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"IsShaded\" = 1 AND (\"DPS\" IS NOT NULL OR \"DatapointSignature\" IS NOT NULL)");
        Assert.Equal(0, shadedWithSignature);

        var unshadedWithoutSignature = ScalarLong(
            "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"IsShaded\" = 0 AND (\"DPS\" IS NULL OR \"DatapointSignature\" IS NULL)");
        Assert.Equal(0, unshadedWithoutSignature);

        var unshadedTotal = ScalarLong("SELECT COUNT(*) FROM \"mTableCell\" WHERE \"IsShaded\" = 0");
        var shadedTotal = ScalarLong("SELECT COUNT(*) FROM \"mTableCell\" WHERE \"IsShaded\" = 1");
        Assert.True(unshadedTotal > 0, "There is no non-shaded cell: the invariant would be empty.");
        Assert.True(shadedTotal > 0, "There is no shaded cell: half of the invariant would be empty.");
        Assert.Equal(total, unshadedTotal + shadedTotal);
    }

    // ------------------------------------------------------------------
    // Invariant 2 - DatapointSignature = DMS, with the numeric HierarchyID inside the bracket,
    // whereas DPS carries the hierarchy code. The two can ONLY diverge inside a '(*[...]' bracket.
    // Verified literally IN THE REFERENCE 4.2 ITSELF (not only in the generated output): its
    // mTableCell has EXACTLY 28,065 rows with DPS != DatapointSignature -- the same number, digit
    // for digit, as the rows with a bracket -- so a "literally equal always" assertion would be
    // wrong even AGAINST the reference; it is not a real property of the schema.
    // ------------------------------------------------------------------

    [DataFact]
    public void DatapointSignature_EqualsDps_ExceptInsideTheOpenAxisWithRestrictionBracket()
    {
        var rows = QueryHelpers.Rows(
            fixture.GeneratedConnection,
            "SELECT \"CellID\", \"DPS\", \"DatapointSignature\" FROM \"mTableCell\" WHERE \"DPS\" IS NOT NULL",
            3);

        Assert.True(rows.Count > 0, "There is no cell with a non-null DPS: the invariant would be empty.");

        var mismatches = rows.Where(r => !string.Equals(r[1], r[2], StringComparison.Ordinal)).ToList();

        // EVERY divergence must carry the '(*[' pattern in both strings -- otherwise it is a
        // divergence OUTSIDE the bracket, and that is a failure (see also the stricter pair-by-pair
        // check of Dpm20OpenAxisValueRestrictionTests.ComposeDpsAndComposeDms...).
        var outsideBracket = mismatches.Where(r => !r[1]!.Contains("(*[", StringComparison.Ordinal) || !r[2]!.Contains("(*[", StringComparison.Ordinal)).ToList();
        Assert.True(
            outsideBracket.Count == 0,
            $"{outsideBracket.Count}/{mismatches.Count} DatapointSignature/DPS divergences WITHOUT the '(*[' pattern: "
            + string.Join(" | ", outsideBracket.Take(10).Select(r => $"CellID={r[0]}: DPS='{r[1]}' DatapointSignature='{r[2]}'")));

        // And EVERY row with the bracket pattern in the DPS must carry a divergence (the sentinel of
        // an open axis WITH restriction ALWAYS distinguishes code from ID).
        var bracketedButEqual = rows.Where(r => r[1]!.Contains("(*[", StringComparison.Ordinal) && string.Equals(r[1], r[2], StringComparison.Ordinal)).ToList();
        Assert.True(
            bracketedButEqual.Count == 0,
            $"{bracketedButEqual.Count} rows with a bracket in the DPS but DatapointSignature EQUAL to the DPS (the numeric HierarchyID was expected): "
            + string.Join(" | ", bracketedButEqual.Take(10).Select(r => $"CellID={r[0]}: DPS='{r[1]}'")));

        Assert.Equal(28065, mismatches.Count); // measured: exactly the rows with a bracket
    }

    // ------------------------------------------------------------------
    // Invariant 3 - ALL cells carry the BusinessCode, shaded ones included.
    // ------------------------------------------------------------------

    [DataFact]
    public void BusinessCode_IsCarriedByAllCells_ShadedOnesIncluded()
    {
        var total = ScalarLong("SELECT COUNT(*) FROM \"mTableCell\"");
        Assert.Equal(163218, total);

        var withoutBusinessCode = ScalarLong("SELECT COUNT(*) FROM \"mTableCell\" WHERE \"BusinessCode\" IS NULL");
        Assert.Equal(0, withoutBusinessCode);

        var shadedWithBusinessCode = ScalarLong(
            "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"IsShaded\" = 1 AND \"BusinessCode\" IS NOT NULL");
        var shadedTotal = ScalarLong("SELECT COUNT(*) FROM \"mTableCell\" WHERE \"IsShaded\" = 1");
        Assert.True(shadedTotal > 0, "There is no shaded cell: the check would be empty.");
        Assert.Equal(shadedTotal, shadedWithBusinessCode);
    }

    // ------------------------------------------------------------------
    // Invariant 4 - MET(...) ALWAYS comes first in the DPS, and the rest is ordered ordinally
    // (StringComparer.Ordinal) by dimension XBRL code. Over ALL signatures (~97,678).
    // ------------------------------------------------------------------

    [DataFact]
    public void MetAlwaysComesFirst_AndTheRestIsOrderedOrdinally_OverAllSignatures()
    {
        var rows = QueryHelpers.Rows(fixture.GeneratedConnection, "SELECT \"CellID\", \"DPS\" FROM \"mTableCell\" WHERE \"DPS\" IS NOT NULL", 2);
        Assert.True(rows.Count > 60000, $"Only {rows.Count} rows with DPS: the check does not cover the expected volume.");

        var failures = new List<string>();
        foreach (var row in rows)
        {
            var cellId = row[0];
            var dps = row[1]!;
            var parts = dps.Split('|');

            var metParts = parts.Where(p => p.StartsWith("MET(", StringComparison.Ordinal)).ToList();
            if (metParts.Count != 1)
            {
                failures.Add($"CellID={cellId}: {metParts.Count} MET(...) pairs in '{dps}' (exactly 1 expected)");
                continue;
            }

            if (!parts[0].StartsWith("MET(", StringComparison.Ordinal))
            {
                failures.Add($"CellID={cellId}: MET(...) does not come first in '{dps}'");
                continue;
            }

            var rest = parts.Skip(1).ToList();
            var dimCodes = rest.Select(p =>
            {
                var idx = p.IndexOf('(');
                return idx > 0 ? p[..idx] : p;
            }).ToList();

            var sortedOrdinal = dimCodes.OrderBy(c => c, StringComparer.Ordinal).ToList();
            if (!dimCodes.SequenceEqual(sortedOrdinal, StringComparer.Ordinal))
            {
                failures.Add($"CellID={cellId}: NOT in ascending ordinal order: [{string.Join(",", dimCodes)}] in '{dps}'");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count}/{rows.Count} signatures violate 'MET first + rest in ascending ordinal order':\n" + string.Join("\n", failures.Take(30)));
    }

    // ------------------------------------------------------------------
    // Invariant 5 - No signature contains a default member, except inside MET(...).
    // It is the algorithm step that discards default members, which on its own is worth 22%.
    // ------------------------------------------------------------------

    [DataFact]
    public void NoSignature_ContainsADefaultMember_ExceptInsideMet()
    {
        var defaultMemberCodes = QueryHelpers.Rows(
                fixture.GeneratedConnection,
                "SELECT \"MemberXBRLCode\" FROM \"mMember\" WHERE \"IsDefaultMember\" = 1 AND \"MemberXBRLCode\" IS NOT NULL",
                1)
            .Select(r => r[0]!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(defaultMemberCodes.Count > 0, "There is no default member in mMember: the invariant would check nothing.");

        var rows = QueryHelpers.Rows(fixture.GeneratedConnection, "SELECT \"CellID\", \"DPS\" FROM \"mTableCell\" WHERE \"DPS\" IS NOT NULL", 2);
        Assert.True(rows.Count > 60000);

        var failures = new List<string>();
        foreach (var row in rows)
        {
            var cellId = row[0];
            var dps = row[1]!;
            var parts = dps.Split('|');
            var rest = parts.Where(p => !p.StartsWith("MET(", StringComparison.Ordinal));

            foreach (var pair in rest)
            {
                var open = pair.IndexOf('(');
                var close = pair.LastIndexOf(')');
                if (open < 0 || close < 0 || close <= open)
                {
                    failures.Add($"CellID={cellId}: pair without well-formed parentheses in '{dps}' ('{pair}')");
                    continue;
                }

                var value = pair[(open + 1)..close];
                if (value.StartsWith('*'))
                {
                    continue; // open axis (unrestricted or semi-open): not a member
                }

                if (defaultMemberCodes.Contains(value))
                {
                    failures.Add($"CellID={cellId}: NON-MET pair '{pair}' uses the default member '{value}' in '{dps}'");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count}/{rows.Count} signatures with a default member OUTSIDE MET(...):\n"
            + string.Join("\n", failures.Take(30)));
    }

    // ------------------------------------------------------------------
    // Invariant 6 - The BusinessCode has the form {table,...} and its first component is the
    // TableCode of the cell, in all 163,218 rows.
    // ------------------------------------------------------------------

    [DataFact]
    public void BusinessCode_HasTheFormTableEtc_AndItsFirstComponentIsTheCellTableCode_In163218Rows()
    {
        var rows = QueryHelpers.Rows(
            fixture.GeneratedConnection,
            """
            SELECT c."CellID", c."BusinessCode", t."TableCode"
            FROM "mTableCell" c
            JOIN "mTable" t ON t."TableID" = c."TableID"
            """,
            3);

        Assert.Equal(163218, rows.Count);

        var failures = new List<string>();
        foreach (var row in rows)
        {
            var cellId = row[0];
            var businessCode = row[1]!;
            var tableCode = row[2]!;

            if (!businessCode.StartsWith('{') || !businessCode.EndsWith('}'))
            {
                failures.Add($"CellID={cellId}: BusinessCode '{businessCode}' does not have the form '{{...}}'");
                continue;
            }

            var firstComponent = businessCode[1..^1].Split(',')[0];
            if (!string.Equals(firstComponent, tableCode, StringComparison.Ordinal))
            {
                failures.Add($"CellID={cellId}: first component '{firstComponent}' != TableCode '{tableCode}' (BusinessCode='{businessCode}')");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count}/163218 BusinessCode values that do NOT have the form '{{TableCode,...}}':\n" + string.Join("\n", failures.Take(30)));
    }

    // ------------------------------------------------------------------
    // Against the reference (by business key, NEVER by CellID): BusinessCode,
    // (TableCode, BusinessCode), MINIMUM threshold -- never exact equality (the secondary ordering
    // within a single orientation is a declared choice, not derived; about 1,183/144,463 do not
    // match for that reason, and it is not a failure of this check).
    // ------------------------------------------------------------------

    [DataFact]
    public void BusinessCode_MatchesReference42_AtLeast99Percent_ByTableBusinessCodeKey()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var generatedPairs = QueryHelpers.Rows(
                fixture.GeneratedConnection,
                """
                SELECT t."TableCode", c."BusinessCode"
                FROM "mTableCell" c
                JOIN "mTable" t ON t."TableID" = c."TableID"
                """,
                2)
            .Select(r => (TableCode: r[0]!, BusinessCode: r[1]!))
            .ToHashSet();

        Assert.True(generatedPairs.Count > 140000, $"Only {generatedPairs.Count} generated (TableCode,BusinessCode) pairs: unexpectedly low volume.");

        using var referenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);
        var referencePairs = QueryHelpers.Rows(
                referenceConnection,
                """
                SELECT t."TableCode", c."BusinessCode"
                FROM "mTableCell" c
                JOIN "mTable" t ON t."TableID" = c."TableID"
                """,
                2)
            .Select(r => (TableCode: r[0]!, BusinessCode: r[1]!))
            .ToHashSet();

        Assert.True(referencePairs.Count > 140000, $"Only {referencePairs.Count} (TableCode,BusinessCode) pairs in reference 4.2: unexpectedly low volume.");

        var matched = referencePairs.Intersect(generatedPairs).Count();

        var ratio = (double)matched / referencePairs.Count;

        Assert.True(
            ratio >= 0.99,
            $"BusinessCode matched by (TableCode,BusinessCode) against EBA_4.2_Hotfix.db: {matched}/{referencePairs.Count} = {ratio:P4}, "
            + "below the 99% minimum threshold (the secondary ordering within a single orientation is a declared choice, not derived; "
            + "~1,183/144,463 do not match for that reason and it is a known residue, not chaseable to 100%).");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private long ScalarLong(string sql) => QueryHelpers.Scalar(fixture.GeneratedConnection, sql);

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
