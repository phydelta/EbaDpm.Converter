using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Tests.Dpm2;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Cli;

/// <summary>
/// #73: <c>--release</c>, the removal of <c>--cutoff-release</c>, and the DPM 1.0-only selectors
/// (<c>--all</c>, <c>--taxonomies</c>, <c>--taxonomykeys</c>, <c>--releases</c>) with a DPM 2.0 source.
/// The tests that need a DPM 2.0 source use a tiny synthetic <c>.accdb</c> (<see cref="AceFactAttribute"/>):
/// they stop before any conversion, which is what each one asserts (nothing was written).
/// </summary>
[Trait("Tier", "Synthetic")]
public sealed class CliReleaseTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), $"EbaDpm.CliRelease_{Environment.ProcessId}_{Guid.NewGuid():N}");

    public CliReleaseTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory: a locked file must not mask the test verdict.
        }
    }

    private static (int Exit, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliRunner.Run(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    // ------------------------------------------------------------------
    // Argument handling that needs neither data nor a source
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("convert")]
    [InlineData("list")]
    public void CutoffReleaseOption_NoLongerExists_AndIsAnUnrecognizedArgument(string mode)
    {
        var args = mode == "convert"
            ? new[] { "--source", "does-not-exist.accdb", "--output", "out", "--cutoff-release", "4.2" }
            : ["--list-taxonomies", "--source", "does-not-exist.accdb", "--cutoff-release", "4.2"];

        var (exit, _, stderr) = Run(args);

        Assert.Equal(CliRunner.ExitArgumentError, exit);
        Assert.Contains("unrecognized argument", stderr, StringComparison.Ordinal);
        Assert.Contains("--cutoff-release", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("convert")]
    [InlineData("list")]
    public void ReleaseOption_IsRecognized_PositiveControlOfTheRemovalAbove(string mode)
    {
        // Same shape as the test above with --release: it is NOT reported as unrecognized, it
        // fails later, on the source that does not exist.
        var args = mode == "convert"
            ? new[] { "--source", "does-not-exist.accdb", "--output", "out", "--release", "4.2" }
            : ["--list-taxonomies", "--source", "does-not-exist.accdb", "--release", "4.2"];

        var (exit, _, stderr) = Run(args);

        Assert.Equal(CliRunner.ExitArgumentError, exit);
        Assert.DoesNotContain("unrecognized argument", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("convert")]
    [InlineData("list")]
    public void ReleaseOption_WithoutValue_IsAnArgumentError(string mode)
    {
        var args = mode == "convert"
            ? new[] { "--source", "does-not-exist.accdb", "--output", "out", "--release" }
            : ["--list-taxonomies", "--source", "does-not-exist.accdb", "--release"];

        var (exit, _, stderr) = Run(args);

        Assert.Equal(CliRunner.ExitArgumentError, exit);
        Assert.Contains("argument --release requires a value.", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseOption_FollowedByAnotherOption_IsAnArgumentError()
    {
        var (exit, _, stderr) = Run("--source", "does-not-exist.accdb", "--output", "out", "--release", "--overwrite");

        Assert.Equal(CliRunner.ExitArgumentError, exit);
        Assert.Contains("argument --release requires a value.", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_DocumentsRelease_AndNoLongerTheCutoffRelease()
    {
        var (exit, stdout, _) = Run("--help");

        Assert.Equal(CliRunner.ExitOk, exit);
        Assert.Contains("--release <code>", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--cutoff-release", stdout, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // DPM 2.0 source (synthetic .accdb: releases 4.0 (1 module version), 4.1 (only a document
    // module: empty), 4.2 (2 module versions)). Nothing here reaches a conversion.
    // ------------------------------------------------------------------

    private string NewSource() => Dpm20ReleaseCatalogTests.CreateCatalogSourceForCli();

    [AceTheory]
    [InlineData("--all", null)]
    [InlineData("--taxonomies", "corep 4.2")]
    [InlineData("--taxonomykeys", "corep/4.2")]
    [InlineData("--releases", "4.2")]
    public void Dpm20Source_WithADpm10OnlySelector_IsAnArgumentError_NamingTheOption_AndWritesNothing(string option, string? value)
    {
        var accdb = NewSource();
        try
        {
            var output = Path.Join(_directory, "out-" + option.TrimStart('-'));
            var args = new List<string> { "--source", accdb, "--output", output, option };
            if (value is not null)
            {
                args.Add(value);
            }

            var (exit, _, stderr) = Run([.. args]);

            Assert.Equal(CliRunner.ExitArgumentError, exit);
            Assert.Contains($"{option} does not apply to DPM 2.0 sources", stderr, StringComparison.Ordinal);
            Assert.False(Directory.Exists(output) || File.Exists(output), "A rejected selector must not create the output.");
        }
        finally
        {
            Dpm2SyntheticSource.Delete(accdb);
        }
    }

    [AceFact]
    public void Dpm20Source_WithoutASelector_IsNotRejectedAsASelectorError()
    {
        // Positive control of the test above: the same source and shape without a selector goes past
        // the selector check. It stops on the explicit unknown release, which is a different error.
        var accdb = NewSource();
        try
        {
            var (exit, _, stderr) = Run("--source", accdb, "--output", Path.Join(_directory, "o.db"), "--release", "9.9");

            Assert.Equal(CliRunner.ExitArgumentError, exit);
            Assert.DoesNotContain("does not apply to DPM 2.0 sources", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Dpm2SyntheticSource.Delete(accdb);
        }
    }

    [AceTheory]
    [InlineData("convert")]
    [InlineData("list")]
    public void Dpm20Source_UnknownRelease_IsAnArgumentError_ListingTheAvailableCodes(string mode)
    {
        var accdb = NewSource();
        try
        {
            var output = Path.Join(_directory, "unknown.db");
            var (exit, _, stderr) = mode == "convert"
                ? Run("--source", accdb, "--output", output, "--release", "9.9")
                : Run("--list-taxonomies", "--source", accdb, "--release", "9.9");

            Assert.Equal(CliRunner.ExitArgumentError, exit);
            Assert.Contains("unknown release '9.9'", stderr, StringComparison.Ordinal);
            // Every release of the source, in ReleaseID order, the empty one marked.
            Assert.Contains("4.0, 4.1 (no exportable module version), 4.2", stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
        }
        finally
        {
            Dpm2SyntheticSource.Delete(accdb);
        }
    }

    [AceTheory]
    [InlineData("convert")]
    [InlineData("list")]
    public void Dpm20Source_ExplicitEmptyRelease_IsAnArgumentError_AndNothingIsWritten(string mode)
    {
        var accdb = NewSource();
        try
        {
            var output = Path.Join(_directory, "empty.db");
            var (exit, _, stderr) = mode == "convert"
                ? Run("--source", accdb, "--output", output, "--release", "4.1")
                : Run("--list-taxonomies", "--source", accdb, "--release", "4.1");

            Assert.Equal(CliRunner.ExitArgumentError, exit);
            Assert.Contains("release '4.1' has no exportable module version", stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
        }
        finally
        {
            Dpm2SyntheticSource.Delete(accdb);
        }
    }

    [AceFact]
    public void Dpm20Source_DirectoryMode_NamesTheFilesByRelease_SkipsTheEmptyOne_AndChecksEveryTargetBeforeWriting()
    {
        var accdb = NewSource();
        try
        {
            var output = Path.Join(_directory, "per-release");
            Directory.CreateDirectory(output);
            var existing = Path.Join(output, "dpm-4.2.db");
            File.WriteAllText(existing, "sentinel, not a database");
            var before = File.ReadAllBytes(existing);

            var (exit, stdout, stderr) = Run("--source", accdb, "--output", output);

            Assert.Equal(CliRunner.ExitArgumentError, exit);
            // The existing target is named with its dpm-<code>.db name, and nothing else was written:
            // dpm-4.0.db comes BEFORE the colliding file in release order and must not exist.
            Assert.Contains(existing, stderr, StringComparison.Ordinal);
            Assert.Contains("nothing was written", stderr, StringComparison.Ordinal);
            Assert.Equal([existing], Directory.GetFiles(output));
            Assert.Equal(before, File.ReadAllBytes(existing));
            // The empty release is reported, by name, and gets no file.
            Assert.Contains("Release 4.1: no exportable module version starts in it; skipped.", stdout, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Join(output, "dpm-4.1.db")));
        }
        finally
        {
            Dpm2SyntheticSource.Delete(accdb);
        }
    }

    [AceFact]
    public void Dpm20Source_DirectoryMode_PreCheckListsEachExistingTarget_AndOnlyThose()
    {
        var accdb = NewSource();
        try
        {
            var output = Path.Join(_directory, "two-existing");
            Directory.CreateDirectory(output);
            var first = Path.Join(output, "dpm-4.0.db");
            var last = Path.Join(output, "dpm-4.2.db");
            File.WriteAllText(first, "x");
            File.WriteAllText(last, "y");

            var (exit, _, stderr) = Run("--source", accdb, "--output", output);

            Assert.Equal(CliRunner.ExitArgumentError, exit);
            Assert.Contains(first, stderr, StringComparison.Ordinal);
            Assert.Contains(last, stderr, StringComparison.Ordinal);
            // The empty release has no target, so it cannot be listed as a collision.
            Assert.DoesNotContain("dpm-4.1.db", stderr, StringComparison.Ordinal);
            Assert.Equal(["dpm-4.0.db", "dpm-4.2.db"], Directory.GetFiles(output).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
        }
        finally
        {
            Dpm2SyntheticSource.Delete(accdb);
        }
    }

    [AceFact]
    public void Dpm20Source_DirectoryMode_OutputThatIsAFile_IsAnArgumentError()
    {
        var accdb = NewSource();
        try
        {
            var output = Path.Join(_directory, "a-file.db");
            File.WriteAllText(output, "x");

            var (exit, _, stderr) = Run("--source", accdb, "--output", output);

            Assert.Equal(CliRunner.ExitArgumentError, exit);
            Assert.Contains("is a file; without --release it must be a directory", stderr, StringComparison.Ordinal);
            Assert.Equal("x", File.ReadAllText(output));
        }
        finally
        {
            Dpm2SyntheticSource.Delete(accdb);
        }
    }

    [AceTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dpm20Source_WithRelease_OutputThatIsAnExistingDirectory_IsAnArgumentError_AndNothingIsWritten(bool overwrite)
    {
        var accdb = NewSource();
        try
        {
            var output = Path.Join(_directory, "existing-dir");
            Directory.CreateDirectory(output);

            var args = new List<string> { "--source", accdb, "--output", output, "--release", "4.2" };
            if (overwrite)
            {
                args.Add("--overwrite");
            }

            var (exit, _, stderr) = Run([.. args]);

            Assert.Equal(CliRunner.ExitArgumentError, exit);
            Assert.Contains(output, stderr, StringComparison.Ordinal);
            Assert.Contains("is a directory", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("SqliteException", stderr, StringComparison.Ordinal);
            Assert.True(Directory.Exists(output));
            Assert.Empty(Directory.GetFileSystemEntries(output));
        }
        finally
        {
            Dpm2SyntheticSource.Delete(accdb);
        }
    }

    [AceFact]
    public void Dpm20Source_DirectoryMode_WhenNoReleaseHasAnExportableModuleVersion_IsAnArgumentError()
    {
        // Only a document module version: every release is empty.
        var accdb = Dpm2SyntheticSource.Create(c =>
        {
            Dpm2SyntheticSource.Exec(c, "INSERT INTO [Release] ([ReleaseID],[Code],[IsCurrent]) VALUES (1,'4.0',-1)");
            Dpm2SyntheticSource.Exec(c, "INSERT INTO [Module] VALUES (11,2,-1)");
            Dpm2SyntheticSource.Exec(c, "INSERT INTO [ModuleVersion] ([ModuleVID],[ModuleID],[Code],[Name],[StartReleaseID]) VALUES (101,11,'d','D',1)");
        });
        try
        {
            var output = Path.Join(_directory, "none");

            var (exit, stdout, stderr) = Run("--source", accdb, "--output", output);

            Assert.Equal(CliRunner.ExitArgumentError, exit);
            Assert.Contains("no release has an exportable module version", stderr, StringComparison.Ordinal);
            Assert.Contains("Release 4.0: no exportable module version starts in it; skipped.", stdout, StringComparison.Ordinal);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            Dpm2SyntheticSource.Delete(accdb);
        }
    }
}

/// <summary><c>--release</c> with a DPM 1.0 source is an error, and the DPM 1.0 selectors keep working.</summary>
[Trait("Tier", "RealData")]
public sealed class CliReleaseDpm10DataTests
{
    [DataFact]
    public void ListTaxonomies_OnDpm10Source_WithRelease_IsAnArgumentError()
    {
        RepoPaths.EnsureAccessDatabaseExists();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = CliRunner.Run(
            ["--list-taxonomies", "--source", RepoPaths.AccessDatabasePath, "--release", "4.2"], stdout, stderr);

        Assert.Equal(CliRunner.ExitArgumentError, exitCode);
        Assert.Contains("--release only applies to DPM 2.0 sources.", stderr.ToString(), StringComparison.Ordinal);
    }

    [DataFact]
    public void Convert_OnDpm10Source_WithRelease_IsAnArgumentError_AndCreatesNoOutput()
    {
        RepoPaths.EnsureAccessDatabaseExists();
        var tempDirectory = Path.Join(Path.GetTempPath(), $"EbaDpm.CliReleaseTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var outputPath = Path.Join(tempDirectory, "o.db");
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = CliRunner.Run(
                ["--source", RepoPaths.AccessDatabasePath, "--output", outputPath, "--taxonomies", "COREP 3.2", "--release", "4.2"],
                stdout, stderr);

            Assert.Equal(CliRunner.ExitArgumentError, exitCode);
            Assert.Contains("--release only applies to DPM 2.0 sources.", stderr.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(outputPath), "A rejected --release must not create the output file.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp directory: a file still locked must not mask the test verdict.
            }
        }
    }

    [DataFact]
    public void Convert_OnDpm10Source_WithoutASelector_IsStillAnArgumentError()
    {
        // The selectors stay mandatory for DPM 1.0 (the DPM 2.0 rule must not leak into it).
        RepoPaths.EnsureAccessDatabaseExists();
        var tempDirectory = Path.Join(Path.GetTempPath(), $"EbaDpm.CliReleaseTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = CliRunner.Run(
                ["--source", RepoPaths.AccessDatabasePath, "--output", Path.Join(tempDirectory, "o.db")], stdout, stderr);

            Assert.Equal(CliRunner.ExitArgumentError, exitCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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
}

/// <summary>
/// DPM 2.0 data over the 4.2.1 database, derived from Access (never from a constant): the unknown
/// release message, the listing grouped by release, and the single-release conversion log, using
/// the 4.2 conversion the suite already runs (<see cref="Dpm20SkeletonFixture"/>).
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class CliReleaseDpm20DataTests(Dpm20SkeletonFixture fixture)
{
    private static string Accdb => RepoPaths.AccessDpm20DatabasePath;

    /// <summary>(release code, ReleaseID, non-document module versions starting in it), in ReleaseID order.</summary>
    private static List<(string Code, int Id, long ExportableModuleVersions)> ReleasesFromAccess()
    {
        var releases = AccessCensus.IntByCode(Accdb, "SELECT [Code], [ReleaseID] FROM [Release]")
            .OrderBy(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value, AccessCensus.Scalar(
                Accdb,
                "SELECT COUNT(1) FROM [ModuleVersion] mv INNER JOIN [Module] m ON m.[ModuleID] = mv.[ModuleID] "
                + $"WHERE mv.[StartReleaseID] = {kv.Value} AND m.[isDocumentModule] = 0")))
            .ToList();
        Assert.NotEmpty(releases);
        return releases;
    }

    [DataFact]
    public void Convert_WithRelease_ShowsTheReleaseInTheLogLine_AndTheOutputIsThatRelease()
    {
        Assert.Equal(0, fixture.ExitCode);
        Assert.Contains("Release: 4.2.", fixture.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--cutoff-release", fixture.Stdout, StringComparison.Ordinal);

        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM mRelease WHERE IsCurrent = 1 AND ReleaseCode = '4.2'";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [DataFact]
    public void ListTaxonomies_WithoutRelease_GroupsByRelease_MarksTheEmptyOnes_AndTotalsTheTaxonomiesOfEveryRelease()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = CliRunner.Run(["--list-taxonomies", "--source", Accdb], stdout, stderr);

        Assert.Equal(CliRunner.ExitOk, exitCode);
        Assert.Empty(stderr.ToString());
        var text = stdout.ToString();

        var releases = ReleasesFromAccess();
        long expectedTotal = 0;
        var previousIndex = -1;
        foreach (var (code, _, exportable) in releases)
        {
            var header = $"Release {code} ({exportable} exportable module versions)";
            var index = text.IndexOf(header, StringComparison.Ordinal);
            Assert.True(index > previousIndex, $"Header '{header}' is missing or out of ReleaseID order in:{Environment.NewLine}{text}");
            previousIndex = index;

            var skipped = $"Release {code}: no exportable module version starts in it; skipped.";
            Assert.Equal(exportable == 0, text.Contains(skipped, StringComparison.Ordinal));

            if (exportable > 0)
            {
                expectedTotal += AccessCensus.OfRelease(AccessCensus.EmittedModuleCodesByTaxonomy(Accdb, code), code).Count;
            }
        }

        Assert.True(expectedTotal > 0, "No taxonomy was derived from Access: the comparison would be vacuous.");
        Assert.Contains($"Total: {expectedTotal} taxonomies.", text, StringComparison.Ordinal);
    }

    [DataFact]
    public void ListTaxonomies_WithRelease_ListsOnlyThatRelease()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = CliRunner.Run(
            ["--list-taxonomies", "--source", Accdb, "--release", RepoPaths.Cutoff42ReleaseCode], stdout, stderr);

        Assert.Equal(CliRunner.ExitOk, exitCode);
        Assert.Empty(stderr.ToString());
        var expected = AccessCensus.OfRelease(
            AccessCensus.EmittedModuleCodesByTaxonomy(Accdb, RepoPaths.Cutoff42ReleaseCode), RepoPaths.Cutoff42ReleaseCode);
        Assert.NotEmpty(expected);
        Assert.Contains($"Total: {expected.Count} taxonomies.", stdout.ToString(), StringComparison.Ordinal);
        // Every taxonomy printed is of release 4.2, and the per-release headers are not printed.
        foreach (var code in expected.Keys)
        {
            Assert.Contains(code, stdout.ToString(), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("exportable module versions)", stdout.ToString(), StringComparison.Ordinal);
    }

    [DataFact]
    public void ListTaxonomies_WithUnknownRelease_IsAnArgumentError_ListingTheAvailableCodes()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = CliRunner.Run(["--list-taxonomies", "--source", Accdb, "--release", "9.9"], stdout, stderr);

        Assert.Equal(CliRunner.ExitArgumentError, exitCode);
        Assert.Contains("unknown release '9.9'", stderr.ToString(), StringComparison.Ordinal);
        foreach (var (code, _, _) in ReleasesFromAccess())
        {
            Assert.Contains(code, stderr.ToString(), StringComparison.Ordinal);
        }
    }
}
