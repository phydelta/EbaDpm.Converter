using EbaDpm.Converter.Tests.Dictionary;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Verification of <c>mOpenAxisValueRestriction</c> and of the signature bracket. It reuses
/// <see cref="Dpm20SkeletonFixture"/> (collection <c>Dpm2Skeleton</c>): the same <c>--all</c>
/// conversion always runs <c>Dpm20AxisAndCellLoader.Load</c>, so the 755 MB Access database does
/// not need to be re-read.
///
/// It confirms the measured figures: 207 rows, 199 business keys on each side with an intersection
/// of 199 (0/0 orphans), the contract <c>HierarchyStartingMemberID</c> NULL /
/// <c>IsStartingMemberIncluded</c> = 0 in all 207, and <c>DPS</c> rising from 62.42% to 90.02%.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class Dpm20OpenAxisValueRestrictionTests(Dpm20SkeletonFixture fixture)
{
    // ------------------------------------------------------------------
    // Sanity - the measured count: 207, not 205 (the plan of distinct headers undercounts because
    // some plans are repeated in pairs of axes).
    // ------------------------------------------------------------------

    [DataFact]
    public void MOpenAxisValueRestriction_HasTheMeasuredCount_207()
    {
        Assert.Equal(207, CountRows("mOpenAxisValueRestriction"));
    }

    // ------------------------------------------------------------------
    // Internal invariant: no dangling AxisID or HierarchyID. Each AxisID references an OPEN axis
    // that was actually emitted (present in mAxis with IsOpenAxis=1 AND in mTableAxis -- existing in
    // mAxis is not enough, it must be linked to some table), and each HierarchyID a hierarchy that
    // was actually emitted in mHierarchy. Without looking at the reference.
    // ------------------------------------------------------------------

    [DataFact]
    public void EveryAxisID_IsAnOpenAxis_ActuallyEmitted_AndLinkedInMTableAxis()
    {
        Assert.True(CountRows("mOpenAxisValueRestriction") > 0, "There are no rows: the invariant would be empty.");

        QueryHelpers.AssertNoOrphans(fixture.GeneratedConnection, "mOpenAxisValueRestriction", "AxisID", "mAxis", "AxisID");

        var notOpen = ScalarLong(
            """
            SELECT COUNT(*) FROM "mOpenAxisValueRestriction" r
            JOIN "mAxis" a ON a."AxisID" = r."AxisID"
            WHERE a."IsOpenAxis" = 0
            """);
        Assert.True(notOpen == 0, $"{notOpen} rows of mOpenAxisValueRestriction point to a CLOSED axis (IsOpenAxis=0): a dangling axis, which is forbidden.");

        var notLinked = ScalarLong(
            """
            SELECT COUNT(*) FROM "mOpenAxisValueRestriction" r
            WHERE NOT EXISTS (SELECT 1 FROM "mTableAxis" ta WHERE ta."AxisID" = r."AxisID")
            """);
        Assert.True(notLinked == 0, $"{notLinked} rows of mOpenAxisValueRestriction point to an AxisID missing from mTableAxis: an axis that belongs to no table.");
    }

    [DataFact]
    public void EveryHierarchyID_IsAHierarchy_ActuallyEmitted()
    {
        Assert.True(CountRows("mOpenAxisValueRestriction") > 0, "There are no rows: the invariant would be empty.");

        QueryHelpers.AssertNoOrphans(fixture.GeneratedConnection, "mOpenAxisValueRestriction", "HierarchyID", "mHierarchy", "HierarchyID");
    }

    // ------------------------------------------------------------------
    // HierarchyStartingMemberID = NULL and IsStartingMemberIncluded = 0 (FALSE, NOT NULL) in all 207
    // rows - the DPM 2.0 source does not model a starting member.
    // ------------------------------------------------------------------

    [DataFact]
    public void HierarchyStartingMemberID_IsNull_InAllRows()
    {
        var total = CountRows("mOpenAxisValueRestriction");
        Assert.True(total > 0);

        var notNull = ScalarLong("SELECT COUNT(*) FROM \"mOpenAxisValueRestriction\" WHERE \"HierarchyStartingMemberID\" IS NOT NULL");
        Assert.Equal(0, notNull);
    }

    [DataFact]
    public void IsStartingMemberIncluded_IsZero_NeverNull_InAllRows()
    {
        var total = CountRows("mOpenAxisValueRestriction");
        Assert.True(total > 0);

        // "IS NOT 0" in SQLite distinguishes an explicit 0 from NULL (unlike "<> 0", which does not
        // capture NULL). The two checks are kept separate to make clear which of them would fail.
        var notZero = ScalarLong("SELECT COUNT(*) FROM \"mOpenAxisValueRestriction\" WHERE \"IsStartingMemberIncluded\" IS NOT 0");
        Assert.Equal(0, notZero);

        var isNull = ScalarLong("SELECT COUNT(*) FROM \"mOpenAxisValueRestriction\" WHERE \"IsStartingMemberIncluded\" IS NULL");
        Assert.Equal(0, isNull);
    }

    // ------------------------------------------------------------------
    // Against the reference (by business key -- table code, axis code, hierarchy code -- NEVER by
    // AxisID/HierarchyID): 199 keys on each side, intersection 199, zero only-in-reference and zero
    // only-in-generated. Critical layer: exact equality, no threshold.
    // ------------------------------------------------------------------

    [DataFact]
    public void BusinessKey_TableAxisHierarchy_MatchesReference42Exactly_199Of199()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var generatedKeys = RestrictionKeys(fixture.GeneratedConnection);
        using var referenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);
        var referenceKeys = RestrictionKeys(referenceConnection);

        Assert.Equal(199, generatedKeys.Count);
        Assert.Equal(199, referenceKeys.Count);

        var onlyInReference = referenceKeys.Except(generatedKeys).ToList();
        var onlyInGenerated = generatedKeys.Except(referenceKeys).ToList();

        Assert.True(
            onlyInReference.Count == 0,
            $"{onlyInReference.Count} keys (TableCode,AxisCode,HierarchyCode) ONLY in reference 4.2: "
            + string.Join(" | ", onlyInReference.Take(20)));

        Assert.True(
            onlyInGenerated.Count == 0,
            $"{onlyInGenerated.Count} keys (TableCode,AxisCode,HierarchyCode) ONLY in the generated output: "
            + string.Join(" | ", onlyInGenerated.Take(20)));
    }

    private static HashSet<(string TableCode, string AxisCode, string HierarchyCode)> RestrictionKeys(SqliteConnection connection)
    {
        return QueryHelpers.Rows(
                connection,
                """
                SELECT t."TableCode", a."AxisCode", h."HierarchyCode"
                FROM "mOpenAxisValueRestriction" r
                JOIN "mAxis" a ON a."AxisID" = r."AxisID"
                JOIN "mTableAxis" ta ON ta."AxisID" = a."AxisID"
                JOIN "mTable" t ON t."TableID" = ta."TableID"
                JOIN "mHierarchy" h ON h."HierarchyID" = r."HierarchyID"
                """,
                3)
            .Select(row => (TableCode: row[0]!, AxisCode: row[1]!, HierarchyCode: row[2]!))
            .ToHashSet();
    }

    // ------------------------------------------------------------------
    // The signature bracket. Concrete nominal case, quoted literally: CellID 120637 of I_11.03
    // carries TWO open axes in the same cell -- qCOE, WITH a restriction, and qEGT, WITHOUT a
    // restriction -- so the same DPS demonstrates both branches of the algorithm at once.
    // ------------------------------------------------------------------

    [DataFact]
    public void SignatureWithRestriction_CarriesBracket_DimCodeInDps_DimHierarchyIdInDms_NominalCase()
    {
        var row = QueryHelpers.Rows(
                fixture.GeneratedConnection,
                """
                SELECT c."CellID", t."TableCode", c."DPS", c."DatapointSignature"
                FROM "mTableCell" c JOIN "mTable" t ON t."TableID" = c."TableID"
                WHERE c."CellID" = 120637
                """,
                4)
            .SingleOrDefault();

        Assert.True(row is not null, "CellID=120637 does not exist in this generation: the nominal case is no longer valid, another one has to be chosen.");

        var tableCode = row![1];
        var dps = row[2]!;
        var dms = row[3]!;

        Assert.Equal("I_11.03", tableCode);

        // The qCOE dimension carries a restriction: the DPS carries the hierarchy CODE in brackets.
        Assert.Contains("eba_dim_4.2:qCOE(*[new_CO1])", dps);

        // The qEGT dimension has NO restriction in this cell: DIM(*) without brackets, no trace of '['.
        Assert.Contains("eba_dim_4.2:qEGT(*)", dps);
        Assert.DoesNotContain("qEGT(*[", dps);

        // In the DMS the same qCOE pair carries the (numeric) HierarchyID, not the code.
        var dmsQcoePart = dms.Split('|').Single(p => p.StartsWith("eba_dim_4.2:qCOE(", StringComparison.Ordinal));
        var bracketContent = dmsQcoePart[(dmsQcoePart.IndexOf("(*[", StringComparison.Ordinal) + 3)..dmsQcoePart.IndexOf(']')];
        Assert.True(int.TryParse(bracketContent, out _), $"The DMS bracket of qCOE is not numeric: '{bracketContent}' in '{dmsQcoePart}'");

        // And the corresponding DPS carries the CODE, not that same number.
        var dpsQcoePart = dps.Split('|').Single(p => p.StartsWith("eba_dim_4.2:qCOE(", StringComparison.Ordinal));
        Assert.Equal("eba_dim_4.2:qCOE(*[new_CO1])", dpsQcoePart);
    }

    [DataFact]
    public void SignatureWithoutRestriction_NeverCarriesBracket_qCOE_NeverAppearsAsStarWithoutBracketInAnyRow()
    {
        // Complement of the nominal case: if qCOE appeared as DIM(*) WITHOUT a bracket in ANY row,
        // it would prove that the restriction is lost along some path not covered by the nominal
        // case above.
        var count = ScalarLong(
            """SELECT COUNT(*) FROM "mTableCell" WHERE "DPS" LIKE '%qCOE(*)%' ESCAPE '\'""");
        Assert.Equal(0, count);
    }

    // ------------------------------------------------------------------
    // ComposeDps and ComposeDms -- two functions, formerly a single one -- MUST NOT diverge except
    // in the bracket of the semi-open axis. It is checked PAIR BY PAIR (not whole cell), over the
    // ~97,678 cells with a signature: 0 divergences outside the bracket.
    // ------------------------------------------------------------------

    [DataFact]
    public void ComposeDpsAndComposeDms_OnlyDivergeInTheOpenAxisBracket_ZeroDivergencesOutside()
    {
        var rows = QueryHelpers.Rows(fixture.GeneratedConnection, "SELECT \"CellID\", \"DPS\", \"DatapointSignature\" FROM \"mTableCell\" WHERE \"DPS\" IS NOT NULL", 3);
        Assert.True(rows.Count > 60000, $"Only {rows.Count} rows with DPS: the check does not cover the expected volume.");

        var failures = new List<string>();
        var pairCountMismatches = 0;

        foreach (var row in rows)
        {
            var cellId = row[0];
            var dps = row[1]!;
            var dms = row[2]!;

            var dpsParts = dps.Split('|');
            var dmsParts = dms.Split('|');

            if (dpsParts.Length != dmsParts.Length)
            {
                pairCountMismatches++;
                failures.Add($"CellID={cellId}: DPS has {dpsParts.Length} pairs, DMS has {dmsParts.Length}. DPS='{dps}' DMS='{dms}'");
                continue;
            }

            for (var i = 0; i < dpsParts.Length; i++)
            {
                var dp = dpsParts[i];
                var dm = dmsParts[i];
                if (string.Equals(dp, dm, StringComparison.Ordinal))
                {
                    continue;
                }

                // The only admissible divergence: both carry the "(*[...]" pattern and only what is
                // INSIDE the bracket changes -- the "dimXbrl(*[" prefix and the suffix after "]" are
                // identical byte for byte.
                var dpBracket = dp.IndexOf("(*[", StringComparison.Ordinal);
                var dmBracket = dm.IndexOf("(*[", StringComparison.Ordinal);
                if (dpBracket < 0 || dmBracket < 0)
                {
                    failures.Add($"CellID={cellId}: pair {i} diverges WITHOUT the '(*[' pattern: DPS='{dp}' DMS='{dm}'");
                    continue;
                }

                var dpPrefix = dp[..(dpBracket + 3)];
                var dmPrefix = dm[..(dmBracket + 3)];
                var dpSuffix = dp[dp.LastIndexOf(']')..];
                var dmSuffix = dm[dm.LastIndexOf(']')..];

                if (dpPrefix != dmPrefix || dpSuffix != dmSuffix)
                {
                    failures.Add($"CellID={cellId}: pair {i} diverges OUTSIDE the bracket content: DPS='{dp}' DMS='{dm}'");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count}/{rows.Count} cells with DPS/DMS diverging outside the bracket ({pairCountMismatches} with a different pair count):\n"
            + string.Join("\n", failures.Take(30)));
    }

    // ------------------------------------------------------------------
    // The overall DPS: 62.42% -> 90.02%, measured by (TableCode,BusinessCode) against reference 4.2
    // -- universe = keys present on BOTH sides with a non-null DPS in the reference (same criterion
    // as Dpm20SignatureTests.BusinessCode_...). MINIMUM threshold, not equality: two causes remain,
    // unrelated to this check and already known (8,095 pairs; MET vocabulary, 885).
    // ------------------------------------------------------------------

    [DataFact]
    public void Dps_MatchesReference42_AtLeast90Percent_ByTableBusinessCodeKey()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var generatedPairs = DpsPairs(fixture.GeneratedConnection);
        using var referenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);
        var referencePairs = DpsPairs(referenceConnection);

        Assert.True(referencePairs.Count > 90000, $"Only {referencePairs.Count} pairs with DPS in reference 4.2: unexpectedly low volume.");

        var commonKeys = referencePairs.Keys.Intersect(generatedPairs.Keys).ToList();
        Assert.True(commonKeys.Count > 80000, $"Only {commonKeys.Count} common (TableCode,BusinessCode) keys: unexpectedly low volume.");

        var matched = commonKeys.Count(key => generatedPairs[key].Overlaps(referencePairs[key]));
        var ratio = (double)matched / commonKeys.Count;

        Assert.True(
            ratio >= 0.90,
            $"DPS matched by (TableCode,BusinessCode) against EBA_4.2_Hotfix.db: {matched}/{commonKeys.Count} = {ratio:P4}, "
            + "below the 90% minimum threshold (8,095 pairs and 885 of MET vocabulary remain, unrelated to the open-axis restriction).");
    }

    private static Dictionary<(string TableCode, string BusinessCode), HashSet<string>> DpsPairs(SqliteConnection connection)
    {
        var result = new Dictionary<(string, string), HashSet<string>>();
        foreach (var row in QueryHelpers.Rows(
            connection,
            """
            SELECT t."TableCode", c."BusinessCode", c."DPS"
            FROM "mTableCell" c JOIN "mTable" t ON t."TableID" = c."TableID"
            WHERE c."DPS" IS NOT NULL
            """,
            3))
        {
            var key = (row[0]!, row[1]!);
            if (!result.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                result[key] = set;
            }

            set.Add(row[2]!);
        }

        return result;
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
