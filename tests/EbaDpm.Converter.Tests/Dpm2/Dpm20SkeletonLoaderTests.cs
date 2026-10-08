using System.Security.Cryptography;
using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// The DPM 2.0 skeleton (<c>Dpm20SkeletonLoader</c>), verified over the real <c>.db</c> generated
/// by the CLI (<see cref="Dpm20SkeletonFixture"/>), exactly the path a user follows with
/// <c>--all</c>. Figures and values column by column.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class Dpm20SkeletonLoaderTests(Dpm20SkeletonFixture fixture)
{
    // ------------------------------------------------------------------
    // Counts, with --all
    // ------------------------------------------------------------------

    [DataFact]
    // 6 on "DPM2 Database_v 4_2_1.accdb" (5 before): the output lists the releases the database declares, 4.2.1 included.
    public void MRelease_Has6Rows() => Assert.Equal(6, CountRows("mRelease"));

    [DataFact]
    public void MReportingFramework_Has18Rows() => Assert.Equal(18, CountRows("mReportingFramework"));

    [DataFact]
    public void MTaxonomy_Has32Rows() => Assert.Equal(32, CountRows("mTaxonomy"));

    [DataFact]
    public void MOwner_MOwnerParent_MLanguage_HaveTheExpectedCounts()
    {
        Assert.Equal(3, CountRows("mOwner"));
        Assert.Equal(1, CountRows("mOwnerParent"));
        Assert.Equal(1, CountRows("mLanguage"));
    }

    /// <summary><c>RewriteUriID</c> does not start at 1 -- it is 2, 3, 4 and 5 (measured).</summary>
    [DataFact]
    public void MRewriteURI_Has4Rows_WithIdsStartingAt2()
    {
        Assert.Equal(4, CountRows("mRewriteURI"));

        var ids = ReadIntColumn("SELECT RewriteUriID FROM mRewriteURI ORDER BY RewriteUriID");

        Assert.Equal([2, 3, 4, 5], ids);
    }

    [DataFact]
    public void MTaxonomyPackage_HasOneRow_And_ADatabasePropertiesHasTwo_ValidationSyntaxAndSourceModel()
    {
        // Since Dpm20SkeletonLoader adds the source marker that ValidationSourceModelDetector reads,
        // aDatabaseProperties carries TWO rows, not one: the pre-existing "Validation syntax
        // version" and the new "Source model" = "DPM 2.0".
        Assert.Equal(1, CountRows("mTaxonomyPackage"));
        Assert.Equal(2, CountRows("aDatabaseProperties"));

        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM \"aDatabaseProperties\" WHERE \"Property\" = 'Source model' AND \"Value\" = 'DPM 2.0'";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    // ------------------------------------------------------------------
    // mRelease, column by column
    // ------------------------------------------------------------------

    /// <summary>
    /// Expected complete row (ReleaseID, ReleaseCode, Status, PublicationDate, IsCurrent).
    /// <c>PublicationDate</c> is compared as an exact STRING, never as a parsed
    /// <see cref="DateTime"/>: a roundtrip through <c>DateTime</c> would bring in the culture and
    /// time zone of the machine running the test, and the test would stop being deterministic.
    /// </summary>
    public static IEnumerable<object[]> ExpectedReleaseRows()
    {
        yield return [1, "3.4", "released", "2024-02-06", 0L];
        yield return [2, "3.5", "released", "2024-07-11", 0L];
        yield return [3, "4.0", "released", "2024-12-19", 0L];
        yield return [4, "4.1", "released", "2025-04-28", 0L];
        // Status of 4.2 is "released" in "DPM2 Database_v 4_2_1.accdb" ("validation" in the file published before): EBA data.
        yield return [5, "4.2", "released", "2025-10-31", 1L];
        yield return [1010000003, "4.2.1", "validation", "2026-02-15", 0L];
    }

    [DataTheory]
    [MemberData(nameof(ExpectedReleaseRows))]
    public void MRelease_MatchesExactly_ColumnByColumn(
        long releaseId, string releaseCode, string status, string publicationDate, long isCurrent)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText =
            "SELECT ReleaseCode, Status, PublicationDate, IsCurrent FROM mRelease WHERE ReleaseID = $id";
        command.Parameters.AddWithValue("$id", releaseId);

        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), $"There is no mRelease row with ReleaseID={releaseId}.");

        Assert.Equal(releaseCode, reader.GetString(0));
        Assert.Equal(status, reader.GetString(1));

        // Exact STRING, not a parsed date -- it is the only test that would detect a regression
        // that introduced a roundtrip through DateTime.
        var actualPublicationDate = reader.GetString(2);
        Assert.Equal(publicationDate, actualPublicationDate);
        Assert.IsType<string>(actualPublicationDate);

        Assert.Equal(isCurrent, reader.GetInt64(3));
    }

    /// <summary>
    /// <c>ReleaseDescription</c> is MULTILINE text and only exists in 4.1 and 4.2: NULL in the
    /// other three, and in the two that do carry it the line breaks must be preserved literally.
    /// Text measured directly in the source (<c>[Release].[Description]</c> through ACE OLEDB), not
    /// guessed.
    /// </summary>
    [DataFact]
    public void MRelease_ReleaseDescription_IsNullExceptIn41And42_AndPreservesLineBreaks()
    {
        AssertReleaseDescription(releaseId: 1, expected: null);
        AssertReleaseDescription(releaseId: 2, expected: null);
        AssertReleaseDescription(releaseId: 3, expected: null);

        const string expected41 =
            "Changes to Supervisory Benchmarking (MR-RM)\n\n" +
            "New EBA Guidelines MiCAR supervisory reporting requirements\n\n" +
            "Full set of Pillar 3 templates (for Pillar 3 hub)\n\n" +
            "Integration of Instant Payments ITS into DPM and taxonomy";
        AssertReleaseDescription(releaseId: 4, expected: expected41);

        const string expected42 =
            "DPM remodeling \n" +
            "Changes to ITS on Resolution Planning\n" +
            "Changes on MREL decisions\n" +
            "Additional reporting on operational risk own funds requirement\n" +
            "Changes to Supervisory Benchmarking for Market risk";
        AssertReleaseDescription(releaseId: 5, expected: expected42);
    }

    private void AssertReleaseDescription(int releaseId, string? expected)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = "SELECT ReleaseDescription FROM mRelease WHERE ReleaseID = $id";
        command.Parameters.AddWithValue("$id", releaseId);

        var value = command.ExecuteScalar();

        if (expected is null)
        {
            Assert.True(value is null or DBNull, $"NULL was expected in ReleaseDescription for ReleaseID={releaseId}.");
        }
        else
        {
            Assert.Equal(expected, Assert.IsType<string>(value));
            Assert.Contains('\n', (string)value!);
        }
    }

    // ------------------------------------------------------------------
    // mTaxonomy and mReportingFramework
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>TaxonomyLabel</c> stays NULL in all 32 rows: the DPM 2.0 source has NO <c>Taxonomy</c>
    /// table, so there is no data to map -- filling it with the framework <c>Name</c> would be
    /// INVENTING the label of a different object. It is not filled "because it looks better": this
    /// test records the reason so that nobody "fixes" it without rethinking it.
    /// </summary>
    [DataFact]
    public void MTaxonomy_TaxonomyLabel_IsNullInAll32Rows_BecauseTheSourceHasNoTaxonomyTable()
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM mTaxonomy WHERE TaxonomyLabel IS NOT NULL";

        var nonNullCount = Convert.ToInt64(command.ExecuteScalar());

        Assert.Equal(0, nonNullCount);
    }

    /// <summary><c>TechnicalStandard</c>: 18 distinct values for the 32 rows, each one the lowercase code of its framework.</summary>
    [DataFact]
    public void MTaxonomy_TechnicalStandard_Has18DistinctValues_OneLowercaseCodePerFramework()
    {
        var values = ReadStringColumn("SELECT TechnicalStandard FROM mTaxonomy");

        Assert.Equal(32, values.Count);
        Assert.All(values, v => Assert.NotNull(v));

        var distinct = values.Where(v => v is not null).Select(v => v!).Distinct(StringComparer.Ordinal).ToList();
        Assert.Equal(18, distinct.Count);
        Assert.All(distinct, code => Assert.Equal(code, code.ToLowerInvariant()));
    }

    /// <summary>
    /// Against <c>EBA_4.2_Hotfix.db</c>, by CONTAINMENT: the 31 <c>TaxonomyCode</c> values of the
    /// reference must ALL be among our 32, and NONE only in the reference. Never by equality of
    /// censuses -- the extra one is <c>pay 4.1</c>.
    /// </summary>
    [DataFact]
    public void MTaxonomy_ContainsAllReferenceTaxonomyCodes_ByContainment()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var generatedCodes = ReadStringColumn("SELECT TaxonomyCode FROM mTaxonomy")
            .Where(c => c is not null)
            .Select(c => c!)
            .ToHashSet(StringComparer.Ordinal);

        var referenceCodes = ReadReferenceTaxonomyCodes();

        Assert.Equal(31, referenceCodes.Count);

        var onlyInReference = referenceCodes.Except(generatedCodes).ToList();
        Assert.Empty(onlyInReference);
        Assert.True(referenceCodes.IsSubsetOf(generatedCodes));
    }

    /// <summary>
    /// <c>mReportingFramework</c>: 18, with <c>FrameworkCode</c> and <c>FrameworkLabel</c> identical
    /// to those of the reference, 18 of 18 -- CRITICAL layer (dictionary by code). The 2 that drop
    /// out of the 20 in the source are <c>DORA</c> and <c>FINREPCOVID19</c>, named in case they ever
    /// reappear and it has to be reviewed why.
    /// </summary>
    [DataFact]
    public void MReportingFramework_MatchesReference_18Of18_CodeAndLabel()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var generated = ReadFrameworkCodeAndLabel(fixture.GeneratedConnection);

        using var referenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);
        var reference = ReadFrameworkCodeAndLabel(referenceConnection);

        Assert.Equal(18, generated.Count);
        Assert.Equal(18, reference.Count);

        var generatedByCode = generated.ToDictionary(r => r.Code, r => r.Label, StringComparer.Ordinal);
        var referenceByCode = reference.ToDictionary(r => r.Code, r => r.Label, StringComparer.Ordinal);

        Assert.Equal(referenceByCode.Keys.OrderBy(k => k, StringComparer.Ordinal), generatedByCode.Keys.OrderBy(k => k, StringComparer.Ordinal));

        var mismatches = new List<string>();
        foreach (var (code, referenceLabel) in referenceByCode)
        {
            var generatedLabel = generatedByCode[code];
            if (!string.Equals(generatedLabel, referenceLabel, StringComparison.Ordinal))
            {
                mismatches.Add($"{code}: expected '{referenceLabel}', got '{generatedLabel}'");
            }
        }

        Assert.True(mismatches.Count == 0, "FrameworkLabel does not match: " + string.Join(" | ", mismatches));
    }

    [DataFact]
    public void MReportingFramework_ExcludesDoraAndFinrepCovid19()
    {
        var codes = ReadStringColumn("SELECT FrameworkCode FROM mReportingFramework")
            .Where(c => c is not null)
            .Select(c => c!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("DORA", codes);
        Assert.DoesNotContain("FINREPCOVID19", codes);
    }

    // ------------------------------------------------------------------
    // mTaxonomyPackage, field by field against the reference
    // ------------------------------------------------------------------

    [DataFact]
    public void MTaxonomyPackage_MatchesTheReferenceRow_FieldByField()
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText =
            """
            SELECT SchemaLocation, Identifier, Name, Description, Version, Publisher, PublisherURL,
                   PublicationDate, LicenceName, LicenceHref, Lang, PublisherCountry, CopyrightComment
            FROM mTaxonomyPackage
            """;

        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), "mTaxonomyPackage is empty.");

        Assert.Equal(
            "http://xbrl.org/2016/taxonomy-package http://www.xbrl.org/2016/taxonomy-package.xsd",
            reader.GetString(0));
        Assert.Equal(
            "http://www.eba.europa.eu/eu/fr/xbrl/tp/4.2/EBA_XBRL_4.2_Reporting_Frameworks_4.2.0.0.zip",
            reader.GetString(1));
        Assert.Equal(
            "EBA XBRL 4.2 Reporting Frameworks 4.2.0.0 "
            + "(AE_COREP_ESG_FC_FINREP_FP_GSII_IF_IMPRAC_IPU_IRRBB_MICA_MREL_PAY_PILLAR3_REM_RES_SBP)",
            reader.GetString(2));
        Assert.Equal(
            "EBA XBRL 4.2 Reporting Frameworks 4.2.0.0 "
            + "(AE_COREP_ESG_FC_FINREP_FP_GSII_IF_IMPRAC_IPU_IRRBB_MICA_MREL_PAY_PILLAR3_REM_RES_SBP)"
            + ". Requires Dictionary 4.2 or later",
            reader.GetString(3));
        Assert.Equal("4.2.0.0", reader.GetString(4));
        Assert.Equal("European Banking Authority", reader.GetString(5));
        Assert.Equal("http://www.eba.europa.eu/", reader.GetString(6));
        Assert.Equal("2025-10-31", reader.GetString(7));

        // EMPTY string, not NULL -- it is the difference that Assert.True(string.IsNullOrEmpty(...))
        // would swallow without noticing.
        Assert.False(reader.IsDBNull(8), "LicenceName must not be NULL: an empty string is expected.");
        Assert.Equal(string.Empty, reader.GetString(8));
        Assert.False(reader.IsDBNull(9), "LicenceHref must not be NULL: an empty string is expected.");
        Assert.Equal(string.Empty, reader.GetString(9));

        Assert.True(reader.IsDBNull(10), "Lang must be NULL.");
        Assert.True(reader.IsDBNull(11), "PublisherCountry must be NULL.");
        Assert.True(reader.IsDBNull(12), "CopyrightComment must be NULL.");
    }

    // ------------------------------------------------------------------
    // Determinism
    // ------------------------------------------------------------------

    /// <summary>
    /// Two consecutive conversions of the skeleton, with the SAME arguments, must give the same
    /// SHA-256. It is the cheap protection against a <c>PublicationDate</c> slipping through
    /// <see cref="DateTime"/> or against any non-deterministic write order.
    /// </summary>
    [DataFact]
    public void TwoConsecutiveConversions_ProduceTheSameSha256()
    {
        RepoPaths.EnsureAccessDpm20DatabaseExists();

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.Dpm20DeterminismTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var pathA = Path.Combine(tempDirectory, "run-a.db");
            var pathB = Path.Combine(tempDirectory, "run-b.db");

            var exitA = CliRunner.Run(
                ["--source", RepoPaths.AccessDpm20DatabasePath, "--cutoff-release", RepoPaths.Cutoff42ReleaseCode, "--output", pathA, "--all"],
                new StringWriter(), new StringWriter());
            SqliteConnection.ClearAllPools();

            var exitB = CliRunner.Run(
                ["--source", RepoPaths.AccessDpm20DatabasePath, "--cutoff-release", RepoPaths.Cutoff42ReleaseCode, "--output", pathB, "--all"],
                new StringWriter(), new StringWriter());
            SqliteConnection.ClearAllPools();

            Assert.Equal(exitA, exitB);

            var hashA = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pathA)));
            var hashB = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pathB)));

            Assert.Equal(hashA, hashB);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort.
            }
        }
    }

    // ------------------------------------------------------------------
    // The DPM 2.0 conversion is COMPLETE, not an honest but incomplete skeleton.
    //
    // REWRITTEN, not relaxed. The two assertions that used to be here (ExitNotImplemented=2, stderr
    // necessarily non-empty) were NEVER a property of the SCHEMA or of the correct behaviour of
    // the CLI: they were an artefact of the conversion not being complete yet. The proof: the
    // message written to stderr was ALWAYS the same fixed text saying that the conversion of a DPM
    // 2.0 source only included part of the model, with no conditional -- it did not report a real
    // error, it warned about a missing feature. With the full conversion implemented, that warning
    // is no longer true and CliRunner no longer writes it (the happy path writes nothing to
    // stderr). Keeping the assertion "stderr must not stay silent" would have meant demanding that
    // the code keep faking an error that no longer exists: a green assertion because the feature
    // did not exist, not because it described something real. What IS a real property and is kept:
    // exit code 0 (success, and it is not merely pretending to be one -- now it IS), an output
    // file with the real tables populated, and a non-silent stdout (the conversion still leaves a
    // trace of what it did).
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>--all</c> over the DPM 2.0 source writes the REAL skeleton (the nine tables, populated)
    /// and the complete conversion: the exit code is <see cref="CliRunner.ExitOk"/> (0), not
    /// <see cref="CliRunner.ExitNotImplemented"/>.
    /// </summary>
    [DataFact]
    public void Convert_All_WritesTheNineSkeletonTables_AndExitCodeIsZero()
    {
        Assert.Equal(CliRunner.ExitOk, fixture.ExitCode);

        Assert.True(CountRows("mRelease") > 0);
        Assert.True(CountRows("mReportingFramework") > 0);
        Assert.True(CountRows("mTaxonomy") > 0);
        Assert.True(CountRows("mOwner") > 0);
        Assert.True(CountRows("mOwnerParent") > 0);
        Assert.True(CountRows("mLanguage") > 0);
        Assert.True(CountRows("mRewriteURI") > 0);
        Assert.True(CountRows("mTaxonomyPackage") > 0);
        Assert.True(CountRows("aDatabaseProperties") > 0);
    }

    /// <summary>
    /// The console output is NOT a contract (the critical thing is the output SQLite). What IS
    /// BEHAVIOUR and must be protected, even now that the conversion is complete: that it exits
    /// with <see cref="CliRunner.ExitOk"/> (real success, not just a code that does not look like a
    /// failure) and that it leaves a trace on stdout (it cannot stay silent about what it did).
    /// stderr is NO LONGER required to have content: demanding it would mean demanding that the
    /// code write an error warning about a conversion that has no gap left to warn about -- see
    /// the note above on why the old assertion was an artefact, not a property.
    /// </summary>
    [DataFact]
    public void Convert_All_ExitsOk_AndLeavesTracesOnStdout()
    {
        Assert.Equal(CliRunner.ExitOk, fixture.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(fixture.Stdout), "stdout must not stay silent.");
        Assert.Empty(fixture.Stderr);
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

    private static List<(string Code, string? Label)> ReadFrameworkCodeAndLabel(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT FrameworkCode, FrameworkLabel FROM mReportingFramework";
        using var reader = command.ExecuteReader();

        var results = new List<(string, string?)>();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return results;
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

    private static HashSet<string> ReadReferenceTaxonomyCodes()
    {
        using var connection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TaxonomyCode FROM mTaxonomy";

        var codes = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
        }

        return codes;
    }
}
