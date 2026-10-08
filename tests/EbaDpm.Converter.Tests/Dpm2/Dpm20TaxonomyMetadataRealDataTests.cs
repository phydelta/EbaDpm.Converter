using System.Globalization;
using System.Text.RegularExpressions;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Row-wide invariants of the DPM 2.0 <c>mTaxonomy</c> metadata (issue #11). Each violation starts
/// with its invariant id so that the positive controls can show every check able to fail.
/// </summary>
internal static class TaxonomyMetadataInvariants
{
    public const string OpenEnded = "9999-12-31";

    public sealed record Outcome(int Examined, IReadOnlyList<string> Violations);

    public static Outcome Check(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.TaxonomyCode, t.FrameworkID, t.TaxonomyLabel, t.Version, t.PublicationDate, t.FromDate, t.ToDate,
                   t.ExcelTemplate IS NOT NULL,
                   typeof(t.TaxonomyLabel), typeof(t.Version), typeof(t.PublicationDate), typeof(t.FromDate), typeof(t.ToDate),
                   f.FrameworkLabel,
                   (SELECT r.PublicationDate FROM mRelease r WHERE r.ReleaseCode = t.Version)
            FROM mTaxonomy t LEFT JOIN mReportingFramework f ON f.FrameworkID = t.FrameworkID
            ORDER BY t.TaxonomyCode
            """;

        var rows = new List<(string Code, long Fw, string? Label, string? Version, string? Pub, string? From, string? To,
            bool Excel, string[] Types, string? FwLabel, string? ReleaseDate)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? -1 : reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt64(7) != 0,
                    [reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11), reader.GetString(12)],
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14)));
            }
        }

        var violations = new List<string>();
        foreach (var r in rows)
        {
            string[] names = ["TaxonomyLabel", "Version", "PublicationDate", "FromDate", "ToDate"];
            for (var i = 0; i < 5; i++)
            {
                if (r.Types[i] != "text")
                {
                    violations.Add($"I1 {r.Code}: {names[i]} is {r.Types[i]}, expected text");
                }
            }

            if (r.Excel)
            {
                violations.Add($"I8 {r.Code}: ExcelTemplate is not NULL");
            }

            foreach (var (name, value) in new[] { ("PublicationDate", r.Pub), ("FromDate", r.From), ("ToDate", r.To) })
            {
                if (value is not null && !IsIsoDate(value))
                {
                    violations.Add($"I2 {r.Code}: {name}='{value}' is not a valid yyyy-MM-dd date");
                }
            }

            if (r.From is not null && r.To is not null && string.CompareOrdinal(r.From, r.To) > 0)
            {
                violations.Add($"I3 {r.Code}: FromDate {r.From} > ToDate {r.To}");
            }

            var space = r.Code.IndexOf(' ', StringComparison.Ordinal);
            var suffix = space < 0 ? r.Code : r.Code[(space + 1)..];
            if (r.Version != suffix)
            {
                violations.Add($"I5 {r.Code}: Version '{r.Version}' differs from the release code in TaxonomyCode '{suffix}'");
            }

            if (r.Pub != r.ReleaseDate)
            {
                violations.Add($"I6 {r.Code}: PublicationDate '{r.Pub}' differs from mRelease.PublicationDate '{r.ReleaseDate}'");
            }

            if (r.Label is null || r.FwLabel is null
                || !Regex.IsMatch(r.Label, "^" + Regex.Escape(r.FwLabel) + @" \d+(\.\d+)+ \(DPM " + Regex.Escape(suffix) + @"\)$", RegexOptions.CultureInvariant))
            {
                violations.Add($"I7 {r.Code}: label '{r.Label}' is not '<FrameworkLabel> <x.y.z> (DPM {suffix})' (FrameworkLabel '{r.FwLabel}')");
            }
        }

        // I4: per framework, every taxonomy with the latest FromDate is open-ended.
        foreach (var group in rows.Where(r => r.From is not null).GroupBy(r => r.Fw))
        {
            var latest = group.Select(r => r.From!).OrderBy(x => x, StringComparer.Ordinal).Last();
            foreach (var r in group.Where(r => r.From == latest && r.To != OpenEnded))
            {
                violations.Add($"I4 {r.Code}: has the latest FromDate {latest} of its framework but ToDate '{r.To}'");
            }
        }

        return new Outcome(rows.Count, violations);
    }

    public static bool IsIsoDate(string value)
        => Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)
           && DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}

/// <summary>
/// Positive controls (synthetic): every invariant of <see cref="TaxonomyMetadataInvariants"/> finds
/// the mutation built for it, and a clean database yields zero violations over a non-zero universe.
/// </summary>
public sealed class TaxonomyMetadataInvariantControlTests
{
    private static SqliteConnection CleanDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE mReportingFramework (FrameworkID INTEGER, FrameworkCode TEXT, FrameworkLabel TEXT);
            CREATE TABLE mRelease (ReleaseID INTEGER, ReleaseCode TEXT, PublicationDate TEXT);
            CREATE TABLE mTaxonomy (TaxonomyID INTEGER, FrameworkID INTEGER, TaxonomyCode TEXT, TaxonomyLabel TEXT,
                Version TEXT, PublicationDate TEXT, FromDate TEXT, ToDate TEXT, ExcelTemplate BLOB);
            INSERT INTO mReportingFramework VALUES (1, 'COREP', 'Common Reporting'), (2, 'ESG', 'ESG');
            INSERT INTO mRelease VALUES (1, '4.0', '2024-12-19'), (2, '4.2', '2025-10-31'), (3, '4.3', '2026-06-28');
            INSERT INTO mTaxonomy VALUES
              (1, 1, 'corep 4.0', 'Common Reporting 4.0.0 (DPM 4.0)', '4.0', '2024-12-19', '2024-12-31', '2026-03-30', NULL),
              (2, 1, 'corep 4.3', 'Common Reporting 4.1.0 (DPM 4.3)', '4.3', '2026-06-28', '2026-03-31', '9999-12-31', NULL),
              (3, 2, 'esg 4.2',   'ESG 1.3.0 (DPM 4.2)',              '4.2', '2025-10-31', '2026-03-31', '9999-12-31', NULL);
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    [Fact]
    public void CleanDatabase_HasNoViolations_OverANonZeroUniverse()
    {
        using var connection = CleanDatabase();

        var outcome = TaxonomyMetadataInvariants.Check(connection);

        Assert.Equal(3, outcome.Examined);
        Assert.Empty(outcome.Violations);
    }

    [Theory]
    [InlineData("I1", "UPDATE mTaxonomy SET TaxonomyLabel = NULL WHERE TaxonomyID = 1")]
    [InlineData("I1", "UPDATE mTaxonomy SET Version = NULL WHERE TaxonomyID = 1")]
    [InlineData("I1", "UPDATE mTaxonomy SET PublicationDate = NULL WHERE TaxonomyID = 1")]
    [InlineData("I1", "UPDATE mTaxonomy SET FromDate = NULL WHERE TaxonomyID = 1")]
    [InlineData("I1", "UPDATE mTaxonomy SET ToDate = NULL WHERE TaxonomyID = 1")]
    [InlineData("I1", "UPDATE mTaxonomy SET FromDate = x'323032342d31322d3331' WHERE TaxonomyID = 1")]
    [InlineData("I2", "UPDATE mTaxonomy SET FromDate = '2024-13-40' WHERE TaxonomyID = 1")]
    [InlineData("I2", "UPDATE mTaxonomy SET ToDate = '30/03/2026' WHERE TaxonomyID = 1")]
    [InlineData("I2", "UPDATE mTaxonomy SET PublicationDate = '2024-12-19T00:00:00' WHERE TaxonomyID = 1")]
    [InlineData("I3", "UPDATE mTaxonomy SET ToDate = '2024-12-30' WHERE TaxonomyID = 1")]
    [InlineData("I4", "UPDATE mTaxonomy SET ToDate = '2030-01-01' WHERE TaxonomyID = 2")]
    [InlineData("I5", "UPDATE mTaxonomy SET Version = '4.9' WHERE TaxonomyID = 1")]
    [InlineData("I6", "UPDATE mTaxonomy SET PublicationDate = '2024-12-20' WHERE TaxonomyID = 1")]
    [InlineData("I7", "UPDATE mTaxonomy SET TaxonomyLabel = 'Common Reporting (DPM 4.0)' WHERE TaxonomyID = 1")]
    [InlineData("I7", "UPDATE mTaxonomy SET TaxonomyLabel = 'Common Reporting 4.0.0 (DPM 4.2)' WHERE TaxonomyID = 1")]
    [InlineData("I8", "UPDATE mTaxonomy SET ExcelTemplate = x'00' WHERE TaxonomyID = 1")]
    public void EveryInvariant_FindsItsMutation(string expectedId, string mutation)
    {
        using var connection = CleanDatabase();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = mutation;
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var outcome = TaxonomyMetadataInvariants.Check(connection);

        Assert.Contains(outcome.Violations, v => v.StartsWith(expectedId + " ", StringComparison.Ordinal));
    }
}

/// <summary>
/// Real-data tests of the DPM 2.0 <c>mTaxonomy</c> metadata (issue #11). The expected values are in
/// <see cref="TaxonomyMetadataExpected"/>, derived from the Access databases; the anchors in the
/// derived classes are written inline. Everything is compared as exact strings, by TaxonomyCode.
/// </summary>
public abstract class TaxonomyMetadataRealDataTestsBase
{
    protected abstract SqliteConnection Connection { get; }

    protected abstract string ExpectedTable { get; }

    protected void AssertRow(string code, string label, string version, string publicationDate, string from, string to)
    {
        using var command = Connection.CreateCommand();
        command.CommandText =
            "SELECT TaxonomyLabel, Version, PublicationDate, FromDate, ToDate FROM mTaxonomy WHERE TaxonomyCode = $c";
        command.Parameters.AddWithValue("$c", code);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), $"There is no mTaxonomy row with TaxonomyCode '{code}'.");
        var actual = string.Join(" | ", Enumerable.Range(0, 5).Select(i => reader.IsDBNull(i) ? "<NULL>" : reader.GetString(i)));
        Assert.False(reader.Read(), $"More than one row for '{code}'.");
        Assert.Equal(string.Join(" | ", label, version, publicationDate, from, to), actual);
    }

    [DataFact]
    public void EveryRow_MatchesTheExpectedMetadata_ByTaxonomyCode()
    {
        var expected = TaxonomyMetadataExpected.Parse(ExpectedTable);
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var command = Connection.CreateCommand())
        {
            command.CommandText = "SELECT TaxonomyCode, TaxonomyLabel, Version, PublicationDate, FromDate, ToDate FROM mTaxonomy";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                actual.Add(
                    reader.GetString(0),
                    string.Join("|", Enumerable.Range(1, 5).Select(i => reader.IsDBNull(i) ? "<NULL>" : reader.GetString(i))));
            }
        }

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), actual.Keys.OrderBy(k => k, StringComparer.Ordinal));

        var mismatches = expected.Values
            .Select(e => (e.TaxonomyCode, Expected: $"{e.TaxonomyLabel}|{e.Version}|{e.PublicationDate}|{e.FromDate}|{e.ToDate}"))
            .Where(e => actual[e.TaxonomyCode] != e.Expected)
            .Select(e => $"{e.TaxonomyCode}: expected [{e.Expected}] actual [{actual[e.TaxonomyCode]}]")
            .ToList();
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} of {expected.Count} rows differ:{Environment.NewLine}" + string.Join(Environment.NewLine, mismatches));
    }

    [DataFact]
    public void EveryRow_SatisfiesTheMetadataInvariants_OverTheWholeTable()
    {
        var outcome = TaxonomyMetadataInvariants.Check(Connection);

        Assert.Equal(TaxonomyMetadataExpected.Parse(ExpectedTable).Count, outcome.Examined);
        Assert.True(outcome.Violations.Count == 0, string.Join(Environment.NewLine, outcome.Violations));
    }
}

/// <summary>4.3 publication, natural cutoff 4.3 (53 taxonomies).</summary>
[Collection("Dpm2043ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class Dpm2043TaxonomyMetadataTests(Dpm2043ValidateFixture fixture) : TaxonomyMetadataRealDataTestsBase, IDisposable
{
    private SqliteConnection? _connection;

    protected override SqliteConnection Connection => _connection ??= Open(fixture.GeneratedDatabasePath);

    protected override string ExpectedTable => TaxonomyMetadataExpected.Release43;

    internal static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    public void Dispose() => _connection?.Dispose();

    [DataTheory]
    // R' differs from R: nothing starts in 4.3, the greatest start is 4.2.
    [InlineData("corep 4.3", "Common Reporting 4.1.0 (DPM 4.3)", "4.3", "2026-06-28", "2026-03-31", "9999-12-31")]
    [InlineData("corep 4.2", "Common Reporting 4.1.0 (DPM 4.2)", "4.2", "2025-10-31", "2026-03-31", "9999-12-31")]
    // R' = 4.2.1 (finrep starts new module versions there), label version is the one of R'.
    [InlineData("finrep 4.3", "Financial Reporting 1.1.0 (DPM 4.3)", "4.3", "2026-06-28", "2026-03-31", "9999-12-31")]
    [InlineData("finrep 4.2.1", "Financial Reporting 1.1.0 (DPM 4.2.1)", "4.2.1", "2026-02-15", "2026-03-31", "9999-12-31")]
    // ToDate closed by a successor with a later FromDate.
    [InlineData("corep 4.0", "Common Reporting 4.0.0 (DPM 4.0)", "4.0", "2024-12-19", "2024-12-31", "2026-03-30")]
    [InlineData("esg 4.1", "ESG 1.2.0 (DPM 4.1)", "4.1", "2025-04-28", "2025-06-30", "2026-03-30")]
    [InlineData("esg 4.0", "ESG 1.1.0 (DPM 4.0)", "4.0", "2024-12-19", "2024-12-31", "2025-06-29")]
    // Successor with EQUAL FromDate is skipped (rem 4.0 shares 2024-12-31; closed by 4.2).
    [InlineData("rem 3.5", "Remuneration 2.1.0 (DPM 3.5)", "3.5", "2024-07-11", "2024-12-31", "2026-03-30")]
    // Successors with EQUAL FromDate only: stays open.
    [InlineData("pay 4.1", "Payments 1.0.0 (DPM 4.1)", "4.1", "2025-04-28", "2022-12-31", "9999-12-31")]
    // Framework new in 4.3, FromDate in the future.
    [InlineData("tcb 4.3", "Third-country Branches 1.0.0 (DPM 4.3)", "4.3", "2026-06-28", "2027-03-31", "9999-12-31")]
    public void Anchor_Row(string code, string label, string version, string pub, string from, string to)
        => AssertRow(code, label, version, pub, from, to);
}

/// <summary>4.2.1 publication, natural cutoff 4.2.1 (50 taxonomies).</summary>
[Collection("Dpm2042_1ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class Dpm2042_1TaxonomyMetadataTests(Dpm2042_1ValidateFixture fixture) : TaxonomyMetadataRealDataTestsBase, IDisposable
{
    private SqliteConnection? _connection;

    protected override SqliteConnection Connection => _connection ??= Dpm2043TaxonomyMetadataTests.Open(fixture.GeneratedDatabasePath);

    protected override string ExpectedTable => TaxonomyMetadataExpected.Release421Natural;

    public void Dispose() => _connection?.Dispose();

    [DataTheory]
    [InlineData("corep 4.2.1", "Common Reporting 4.1.0 (DPM 4.2.1)", "4.2.1", "2026-02-15", "2026-03-31", "9999-12-31")]
    [InlineData("corep 4.2", "Common Reporting 4.1.0 (DPM 4.2)", "4.2", "2025-10-31", "2026-03-31", "9999-12-31")]
    [InlineData("corep 4.0", "Common Reporting 4.0.0 (DPM 4.0)", "4.0", "2024-12-19", "2024-12-31", "2026-03-30")]
    [InlineData("esg 4.1", "ESG 1.2.0 (DPM 4.1)", "4.1", "2025-04-28", "2025-06-30", "2026-03-30")]
    // finrep starts module versions in 4.2.1 (equal FromDate to 4.2): finrep 4.2 stays open.
    [InlineData("finrep 4.2", "Financial Reporting 3.3.0 (DPM 4.2)", "4.2", "2025-10-31", "2026-03-31", "9999-12-31")]
    [InlineData("finrep 4.2.1", "Financial Reporting 1.1.0 (DPM 4.2.1)", "4.2.1", "2026-02-15", "2026-03-31", "9999-12-31")]
    [InlineData("rem 3.5", "Remuneration 2.1.0 (DPM 3.5)", "3.5", "2024-07-11", "2024-12-31", "2026-03-30")]
    public void Anchor_Row(string code, string label, string version, string pub, string from, string to)
        => AssertRow(code, label, version, pub, from, to);
}

/// <summary>4.2.1 database with the explicit cutoff 4.2 (32 taxonomies): nothing after 4.2 may appear or influence a row.</summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class Dpm2042CutoffTaxonomyMetadataTests(Dpm20SkeletonFixture fixture) : TaxonomyMetadataRealDataTestsBase
{
    protected override SqliteConnection Connection => fixture.GeneratedConnection;

    protected override string ExpectedTable => TaxonomyMetadataExpected.Release42Cutoff;

    [DataTheory]
    [InlineData("corep 4.2", "Common Reporting 4.1.0 (DPM 4.2)", "4.2", "2025-10-31", "2026-03-31", "9999-12-31")]
    [InlineData("corep 4.0", "Common Reporting 4.0.0 (DPM 4.0)", "4.0", "2024-12-19", "2024-12-31", "2026-03-30")]
    [InlineData("esg 4.1", "ESG 1.2.0 (DPM 4.1)", "4.1", "2025-04-28", "2025-06-30", "2026-03-30")]
    // Under cutoff 4.2 the 4.2.1 module versions of finrep do not exist: label version is the 4.2 one.
    [InlineData("finrep 4.2", "Financial Reporting 3.3.0 (DPM 4.2)", "4.2", "2025-10-31", "2026-03-31", "9999-12-31")]
    public void Anchor_Row(string code, string label, string version, string pub, string from, string to)
        => AssertRow(code, label, version, pub, from, to);

    [DataFact]
    public void NoTaxonomy_ExistsForAReleaseAfterTheCutoff()
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM mTaxonomy WHERE Version NOT IN ('3.5', '4.0', '4.1', '4.2')";

        Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
    }
}
