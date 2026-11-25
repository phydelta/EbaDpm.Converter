using System.Globalization;
using System.Text.RegularExpressions;
using EbaDpm.Converter.Distribution.Tests.Schema;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace EbaDpm.Converter.Distribution.Tests.Dpm2;

/// <summary>
/// Real-data tests of the tie-break by consistency of <c>Dpm20HierarchyNodeLoader</c>
/// (docs/mapping-dpm2.md): the number and the identity of the re-anchored nodes are derived from the
/// Access tables by <see cref="HierarchyNodeConsistencyOracle"/> (no pinned figure) and compared by
/// business key with the CLI summary and the generated <c>mHierarchyNode</c>.
///
/// Positive control: the oracle finds cases in 4.3 and 4.4 (a non-empty derivation), so its zero at
/// 4.2 is a result and not a blind spot; the validator-independent SQL check of "no node outside its
/// hierarchy's allowed domains" is evaluated over the same files.
/// </summary>
internal static class ReanchorAssertions
{
    private static readonly Regex SummaryLine = new(
        @"MemberID re-anchored by the consistency tie-break: (\d+) case\(s\)", RegexOptions.CultureInvariant);

    public static int ParseSummaryCount(string stdout)
    {
        var m = SummaryLine.Match(stdout);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    public static void AssertScenario(
        string label, string accdb, string releaseCode, string sqlite, string stdout, ITestOutputHelper output, bool expectCases)
    {
        var cases = HierarchyNodeConsistencyOracle.Derive(accdb, releaseCode, sqlite);

        var perHierarchy = cases.GroupBy(c => c.HierarchyCode).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} {g.Count()}").ToList();
        output.WriteLine($"{label}: derived re-anchored nodes = {cases.Count}: {string.Join(", ", perHierarchy)}");

        // The oracle must be able to find cases where the data has them (positive control of its zero).
        if (expectCases)
        {
            Assert.True(cases.Count > 0, $"{label}: the independent derivation found no conflicting node (it cannot be a positive control).");
        }
        else
        {
            Assert.True(cases.Count == 0, $"{label}: expected no re-anchored node, derived {cases.Count}: {string.Join(", ", perHierarchy)}");
        }

        // The rule needs exactly one candidate; none or several is a gap in the documentation to report.
        var ambiguous = cases.Where(c => c.ExpectedMemberKeys.Count != 1).ToList();
        Assert.True(
            ambiguous.Count == 0,
            $"{label}: {ambiguous.Count} case(s) without exactly one candidate: "
            + string.Join(" | ", ambiguous.Take(10).Select(c => $"{c.HierarchyCode}/ItemID={c.ItemId} natural {c.NaturalMemberKey} -> [{string.Join(",", c.ExpectedMemberKeys)}]")));

        // CLI summary count == derived count.
        var reported = ParseSummaryCount(stdout);
        Assert.True(reported == cases.Count, $"{label}: the CLI reports {reported} re-anchored case(s), the independent derivation {cases.Count}.");

        // The generated nodes, by business key (HierarchyCode, DomainCode:MemberCode).
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = sqlite, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        var nodesByHierarchy = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT h.HierarchyCode, d.DomainCode || ':' || m.MemberCode
                FROM mHierarchyNode n
                JOIN mHierarchy h ON h.HierarchyID = n.HierarchyID
                JOIN mMember m ON m.MemberID = n.MemberID
                JOIN mDomain d ON d.DomainID = m.DomainID
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var h = reader.GetString(0);
                if (!nodesByHierarchy.TryGetValue(h, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    nodesByHierarchy[h] = set;
                }

                set.Add(reader.GetString(1));
            }
        }

        var missing = cases.Where(c => !nodesByHierarchy.TryGetValue(c.HierarchyCode, out var s) || !s.Contains(c.ExpectedMemberKeys[0])).ToList();
        Assert.True(
            missing.Count == 0,
            $"{label}: {missing.Count} re-anchored node(s) absent from the output: "
            + string.Join(" | ", missing.Take(10).Select(c => $"{c.HierarchyCode} expected {c.ExpectedMemberKeys[0]}")));

        var stillNatural = cases.Where(c => nodesByHierarchy[c.HierarchyCode].Contains(c.NaturalMemberKey)).ToList();
        Assert.True(
            stillNatural.Count == 0,
            $"{label}: {stillNatural.Count} node(s) still carry the conflicting member: "
            + string.Join(" | ", stillNatural.Take(10).Select(c => $"{c.HierarchyCode} {c.NaturalMemberKey}")));

        // The consistency relation itself, written independently of the validator.
        using var check = connection.CreateCommand();
        check.CommandText =
            """
            SELECT COUNT(1) FROM mHierarchyNode n
            JOIN mHierarchy h ON h.HierarchyID = n.HierarchyID
            JOIN mMember m ON m.MemberID = n.MemberID
            WHERE m.DomainID <> h.DomainID
              AND NOT EXISTS (SELECT 1 FROM mDomainUnion u WHERE u.UnionDomainID = h.DomainID AND u.UnitedDomainID = m.DomainID)
            """;
        var outside = Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture);
        Assert.True(outside == 0, $"{label}: {outside} node(s) outside the domain of their hierarchy and of its united domains.");
    }
}

[Collection("Dpm2044ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class Dpm20HierarchyNodeReanchor44RealDataTests(Dpm2044ValidateFixture fixture, ITestOutputHelper output)
{
    [DataFact]
    public void Release44_ReanchoredNodes_MatchTheIndependentDerivationFromAccess()
    {
        Assert.Equal(0, fixture.ExitCode);
        ReanchorAssertions.AssertScenario(
            "4.4 database at 4.4", RepoPaths.AccessDpm20Release44DatabasePath, "4.4", fixture.GeneratedDatabasePath, fixture.Stdout, output, expectCases: true);
    }
}

[Collection("Dpm2043ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class Dpm20HierarchyNodeReanchor43RealDataTests(Dpm2043ValidateFixture fixture, ITestOutputHelper output)
{
    [DataFact]
    public void Release43_ReanchoredNodes_MatchTheIndependentDerivationFromAccess()
    {
        Assert.Equal(0, fixture.ExitCode);
        ReanchorAssertions.AssertScenario(
            "4.3 database at 4.3", RepoPaths.AccessDpm20Release43DatabasePath, "4.3", fixture.GeneratedDatabasePath, fixture.Stdout, output, expectCases: true);
    }

    [DataFact]
    public void Release42OfThe43Database_HasNoReanchoredNode_AsDerivedFromAccess()
    {
        var path = fixture.Generated42DatabasePath;
        ReanchorAssertions.AssertScenario(
            "4.3 database at 4.2", RepoPaths.AccessDpm20Release43DatabasePath, "4.2", path, fixture.Generated42Stdout, output, expectCases: false);
    }
}
