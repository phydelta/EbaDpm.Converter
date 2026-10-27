using System.Security.Cryptography;
using System.Globalization;
using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Tests.Infrastructure;
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
    // The output lists the releases the database declares, the publication release (4.2.1) included.
    public void MRelease_HasOneRowPerAccessRelease()
    {
        var expected = AccessCensus.Count(RepoPaths.AccessDpm20DatabasePath, "Release");
        var actual = CountRows("mRelease");
        Assert.True(
            expected == actual,
            $"mRelease rows: expected {expected} (COUNT(1) of [Release] in '{RepoPaths.AccessDpm20DatabaseFileName}'), actual {actual}.");
    }

    /// <summary>Derived from Access: the distinct (lower-case framework, StartRelease.Code) of the emitted module versions.</summary>
    private static Dictionary<string, SortedSet<string>> ExpectedTaxonomies()
        => AccessCensus.EmittedModuleCodesByTaxonomy(RepoPaths.AccessDpm20DatabasePath, RepoPaths.Cutoff42ReleaseCode);

    [DataFact]
    public void MReportingFramework_RowCount_IsTheDistinctFrameworksOfTheDerivedTaxonomies()
    {
        var expected = ExpectedTaxonomies().Keys.Select(k => k[..k.IndexOf(' ', StringComparison.Ordinal)]).Distinct().Count();
        var actual = CountRows("mReportingFramework");
        Assert.True(
            expected == actual,
            $"mReportingFramework rows: expected {expected} (distinct frameworks of the taxonomies derived from '{RepoPaths.AccessDpm20DatabaseFileName}'), actual {actual}.");
    }

    [DataFact]
    public void MTaxonomy_Codes_AreTheDistinctFrameworkAndStartReleaseOfTheEmittedModuleVersions()
    {
        var expected = ExpectedTaxonomies();
        Assert.NotEmpty(expected);
        var actual = ReadStringColumn("SELECT TaxonomyCode FROM mTaxonomy").Where(c => c is not null).Select(c => c!).ToList();

        Assert.Equal(expected.Count, CountRows("mTaxonomy"));
        Assert.Equal(
            expected.Keys.OrderBy(k => k, StringComparer.Ordinal),
            actual.OrderBy(k => k, StringComparer.Ordinal));
    }

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
    /// Every <c>[Release]</c> row of the Access source (ReleaseID, Code, Status, Date) against the
    /// <c>mRelease</c> row with the same <c>ReleaseID</c> (ReleaseCode, Status, PublicationDate,
    /// IsCurrent). <c>PublicationDate</c> is compared as an exact STRING, never as a parsed
    /// <see cref="DateTime"/> on the output side: a roundtrip through <c>DateTime</c> would bring in
    /// the culture and time zone of the machine running the test. The Access date is formatted
    /// <c>yyyy-MM-dd</c> invariantly. <c>IsCurrent</c> is 1 exactly for the CUTOFF release
    /// (<see cref="RepoPaths.Cutoff42ReleaseCode"/>), not for the release Access marks as current
    /// (mapping rule of <c>mRelease</c>, <c>docs/mapping-dpm2.md</c>).
    /// </summary>
    [DataFact]
    public void MRelease_MatchesAccessReleases_ColumnByColumn()
    {
        var accessRows = AccessCensus.Rows(
            RepoPaths.AccessDpm20DatabasePath, "SELECT [ReleaseID], [Code], [Status], [Date] FROM [Release]");
        Assert.True(accessRows.Count > 0, $"[Release] of '{RepoPaths.AccessDpm20DatabaseFileName}' returned no rows: nothing to compare.");

        var problems = new List<string>();
        foreach (var row in accessRows)
        {
            var releaseId = Convert.ToInt64(row[0], CultureInfo.InvariantCulture);
            var code = Convert.ToString(row[1], CultureInfo.InvariantCulture)!;
            var status = Convert.ToString(row[2], CultureInfo.InvariantCulture)!;
            var date = Convert.ToDateTime(row[3], CultureInfo.InvariantCulture).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var isCurrent = code == RepoPaths.Cutoff42ReleaseCode ? 1L : 0L;

            using var command = fixture.GeneratedConnection.CreateCommand();
            command.CommandText =
                "SELECT ReleaseCode, Status, PublicationDate, IsCurrent FROM mRelease WHERE ReleaseID = $id";
            command.Parameters.AddWithValue("$id", releaseId);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                problems.Add($"{code}: no mRelease row with ReleaseID={releaseId}");
                continue;
            }

            if (reader.GetString(0) != code)
            {
                problems.Add($"{code}: ReleaseCode expected '{code}', actual '{reader.GetString(0)}'");
            }

            if (reader.GetString(1) != status)
            {
                problems.Add($"{code}: Status expected '{status}', actual '{reader.GetString(1)}'");
            }

            // Exact STRING, not a parsed date -- it is the only check that would detect a regression
            // that introduced a roundtrip through DateTime.
            if (reader.GetFieldType(2) != typeof(string) || reader.GetString(2) != date)
            {
                problems.Add($"{code}: PublicationDate expected '{date}', actual '{reader.GetValue(2)}'");
            }

            if (reader.GetInt64(3) != isCurrent)
            {
                problems.Add($"{code}: IsCurrent expected {isCurrent}, actual {reader.GetInt64(3)}");
            }
        }

        Assert.True(
            problems.Count == 0,
            $"mRelease differs from [Release] in '{RepoPaths.AccessDpm20DatabaseFileName}' (cutoff {RepoPaths.Cutoff42ReleaseCode}): "
            + string.Join(" | ", problems));
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
    /// Issue #11 retired the former rule "<c>TaxonomyLabel</c> is NULL in every row": the label,
    /// <c>Version</c>, <c>PublicationDate</c>, <c>FromDate</c> and <c>ToDate</c> are now derived
    /// from the <c>ModuleVersion</c> history (<c>docs/mapping-dpm2.md</c>). They are non-NULL TEXT in
    /// every row; <c>ExcelTemplate</c> stays NULL (no source). The exact values are checked in
    /// <c>Dpm2042CutoffTaxonomyMetadataTests</c>.
    /// </summary>
    [DataFact]
    public void MTaxonomy_MetadataColumns_AreNonNullTextInEveryRow_AndExcelTemplateStaysNull()
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1),
                   SUM(typeof(TaxonomyLabel) = 'text' AND typeof(Version) = 'text' AND typeof(PublicationDate) = 'text'
                       AND typeof(FromDate) = 'text' AND typeof(ToDate) = 'text'),
                   SUM(ExcelTemplate IS NULL)
            FROM mTaxonomy
            """;

        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var rows = reader.GetInt64(0);
        Assert.True(
            ExpectedTaxonomies().Count == rows,
            $"mTaxonomy rows: expected {ExpectedTaxonomies().Count} (derived from '{RepoPaths.AccessDpm20DatabaseFileName}'), actual {rows}.");
        Assert.True(reader.GetInt64(1) == rows, $"mTaxonomy rows with all five metadata columns as TEXT: expected {rows} (every row), actual {reader.GetInt64(1)}.");
        Assert.True(reader.GetInt64(2) == rows, $"mTaxonomy rows with ExcelTemplate NULL: expected {rows} (every row), actual {reader.GetInt64(2)}.");
    }

    /// <summary><c>TechnicalStandard</c>: one distinct value per framework of <c>mReportingFramework</c>, each one the lowercase code of its framework.</summary>
    [DataFact]
    public void MTaxonomy_TechnicalStandard_HasOneDistinctValuePerFramework_OneLowercaseCodePerFramework()
    {
        var values = ReadStringColumn("SELECT TechnicalStandard FROM mTaxonomy");

        var taxonomyRows = CountRows("mTaxonomy");
        Assert.True(taxonomyRows == values.Count, $"TechnicalStandard values read from mTaxonomy: expected {taxonomyRows} (mTaxonomy rows), actual {values.Count}.");
        Assert.All(values, v => Assert.NotNull(v));

        var distinct = values.Where(v => v is not null).Select(v => v!).Distinct(StringComparer.Ordinal).ToList();
        var frameworkRows = CountRows("mReportingFramework");
        Assert.True(
            frameworkRows == distinct.Count,
            $"Distinct TechnicalStandard values of mTaxonomy: expected {frameworkRows} (mReportingFramework rows), actual {distinct.Count}.");
        Assert.All(distinct, code => Assert.Equal(code, code.ToLowerInvariant()));
    }

    /// <summary>
    /// Plane B, information only, by CONTAINMENT: every generated <c>TaxonomyCode</c> is among the
    /// reference ones. The reference has more (it keeps the taxonomies of older releases): the ones
    /// we do not generate must be exactly the reference taxonomies that have no module there.
    /// </summary>
    [DataFact]
    public void MTaxonomy_IsContainedInTheReferenceTaxonomyCodes_AndTheOnesLeftOutHaveNoModuleThere()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var generatedCodes = ReadStringColumn("SELECT TaxonomyCode FROM mTaxonomy")
            .Where(c => c is not null)
            .Select(c => c!)
            .ToHashSet(StringComparer.Ordinal);

        var referenceCodes = ReadReferenceTaxonomyCodes();

        ReleasePins.AssertPinned(
            ReleasePins.Reference42, "reference mTaxonomy codes", ReleasePins.Reference42MTaxonomyRows, referenceCodes.Count);

        var onlyGenerated = generatedCodes.Except(referenceCodes).ToList();
        Assert.True(
            onlyGenerated.Count == 0,
            $"Generated taxonomy codes absent from the reference: {string.Join(", ", onlyGenerated)}.");

        using var referenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);
        using var command = referenceConnection.CreateCommand();
        command.CommandText =
            "SELECT t.TaxonomyCode FROM mTaxonomy t WHERE NOT EXISTS (SELECT 1 FROM mModule m WHERE m.TaxonomyID = t.TaxonomyID)";
        var withoutModule = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                withoutModule.Add(reader.GetString(0));
            }
        }

        var onlyInReference = referenceCodes.Except(generatedCodes).OrderBy(c => c, StringComparer.Ordinal).ToList();
        Assert.True(
            onlyInReference.SequenceEqual(withoutModule.OrderBy(c => c, StringComparer.Ordinal)),
            $"Reference taxonomies not generated: [{string.Join(", ", onlyInReference)}]; reference taxonomies without a module: [{string.Join(", ", withoutModule)}].");
    }

    /// <summary>
    /// <c>mReportingFramework</c>: <c>FrameworkCode</c> and <c>FrameworkLabel</c> identical to those
    /// of the reference, every framework -- CRITICAL layer (dictionary by code). The two that drop
    /// out of the source are <c>DORA</c> and <c>FINREPCOVID19</c>, named in case they ever
    /// reappear and it has to be reviewed why.
    /// </summary>
    [DataFact]
    public void MReportingFramework_MatchesReference_CodeAndLabel()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var generated = ReadFrameworkCodeAndLabel(fixture.GeneratedConnection);

        using var referenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);
        var reference = ReadFrameworkCodeAndLabel(referenceConnection);

        var frameworkRows = CountRows("mReportingFramework");
        Assert.True(frameworkRows == generated.Count, $"Frameworks read from the generated mReportingFramework: expected {frameworkRows}, actual {generated.Count}.");
        ReleasePins.AssertPinned(
            ReleasePins.Reference42, "reference mReportingFramework rows", ReleasePins.Reference42MReportingFrameworkRows, reference.Count);

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
        var cutoffDate = Convert.ToDateTime(
                AccessCensus.Rows(
                    RepoPaths.AccessDpm20DatabasePath,
                    $"SELECT [Date] FROM [Release] WHERE [Code] = '{RepoPaths.Cutoff42ReleaseCode}'").Single()[0],
                CultureInfo.InvariantCulture)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var publicationDate = reader.GetString(7);
        Assert.True(
            cutoffDate == publicationDate,
            $"mTaxonomyPackage.PublicationDate: expected '{cutoffDate}' ([Release].[Date] of release {RepoPaths.Cutoff42ReleaseCode} in '{RepoPaths.AccessDpm20DatabaseFileName}'), actual '{publicationDate}'.");

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

        var tempDirectory = Path.Join(Path.GetTempPath(), $"EbaDpm.Dpm20DeterminismTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var pathA = Path.Join(tempDirectory, "run-a.db");
            var pathB = Path.Join(tempDirectory, "run-b.db");

            using var stdoutA = new StringWriter();
            using var stderrA = new StringWriter();
            var exitA = CliRunner.Run(
                ["--source", RepoPaths.AccessDpm20DatabasePath, "--cutoff-release", RepoPaths.Cutoff42ReleaseCode, "--output", pathA, "--all"],
                stdoutA, stderrA);
            SqliteConnection.ClearAllPools();

            using var stdoutB = new StringWriter();
            using var stderrB = new StringWriter();
            var exitB = CliRunner.Run(
                ["--source", RepoPaths.AccessDpm20DatabasePath, "--cutoff-release", RepoPaths.Cutoff42ReleaseCode, "--output", pathB, "--all"],
                stdoutB, stderrB);
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

        Assert.True(CountRows("mRelease") > 0, "mRelease is empty after --all.");
        Assert.True(CountRows("mReportingFramework") > 0, "mReportingFramework is empty after --all.");
        Assert.True(CountRows("mTaxonomy") > 0, "mTaxonomy is empty after --all.");
        Assert.True(CountRows("mOwner") > 0, "mOwner is empty after --all.");
        Assert.True(CountRows("mOwnerParent") > 0, "mOwnerParent is empty after --all.");
        Assert.True(CountRows("mLanguage") > 0, "mLanguage is empty after --all.");
        Assert.True(CountRows("mRewriteURI") > 0, "mRewriteURI is empty after --all.");
        Assert.True(CountRows("mTaxonomyPackage") > 0, "mTaxonomyPackage is empty after --all.");
        Assert.True(CountRows("aDatabaseProperties") > 0, "aDatabaseProperties is empty after --all.");
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
