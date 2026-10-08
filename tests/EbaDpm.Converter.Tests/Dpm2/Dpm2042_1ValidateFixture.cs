using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Tests.Schema;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// The same idea as <see cref="Dpm2043ValidateFixture"/> applied to the 4.2.1 publication
/// (<c>Data/DPM2 Database_v 4_2_1.accdb</c>) converted with its NATURAL cutoff: no
/// <c>--cutoff-release</c>, so the cutoff is the latest release the database declares ("4.2.1").
/// Every other fixture over this file passes the explicit cutoff "4.2" and means "the 4.2
/// release"; this one is the only consumer of the default behaviour.
///
/// It has its own collection so it does not compete with <c>Dpm2Skeleton</c>/<c>Dpm2</c>. It runs
/// <c>--all</c> ONCE through the real CLI and clears the SQLite connection pool before leaving the
/// constructor because <see cref="EbaDpm.Converter.Core.Validation.Validator.Run"/> needs exclusive
/// <c>File.OpenRead</c> access to the same file for the SHA-256. When the data directory is not
/// available the constructor does nothing (the tests are skipped).
/// </summary>
public sealed class Dpm2042_1ValidateFixture : IDisposable
{
    public string TempDirectory { get; } = null!;
    public string GeneratedDatabasePath { get; } = null!;
    public int ExitCode { get; }
    public string Stdout { get; } = null!;
    public string Stderr { get; } = null!;

    /// <summary>Cutoff release code resolved by a natural (no cutoff requested) <see cref="Dpm20AccessReader"/>.</summary>
    public string DerivedCutoffReleaseCode { get; } = null!;

    public bool DerivedCutoffRequested { get; }

    public Dpm2042_1ValidateFixture()
    {
        if (!RepoPaths.IsDataAvailable)
        {
            return;
        }

        RepoPaths.EnsureAccessDpm20DatabaseExists();

        using (var reader = new Dpm20AccessReader(RepoPaths.AccessDpm20DatabasePath))
        {
            reader.Open();
            DerivedCutoffReleaseCode = reader.CutoffReleaseCode;
            DerivedCutoffRequested = reader.CutoffReleaseRequested;
        }

        TempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.Dpm2042_1ValidateSuiteTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDirectory);
        GeneratedDatabasePath = Path.Combine(TempDirectory, "generated-dpm2042_1-validate.db");

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        ExitCode = CliRunner.Run(
            ["--source", RepoPaths.AccessDpm20DatabasePath, "--output", GeneratedDatabasePath, "--all"],
            stdout,
            stderr);

        Stdout = stdout.ToString();
        Stderr = stderr.ToString();

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        try
        {
            if (TempDirectory is not null && Directory.Exists(TempDirectory))
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort: a late SQLite handle must not bring down the test suite.
        }
    }
}

[CollectionDefinition("Dpm2042_1ValidateSuite")]
public sealed class Dpm2042_1ValidateCollection : ICollectionFixture<Dpm2042_1ValidateFixture>
{
}
