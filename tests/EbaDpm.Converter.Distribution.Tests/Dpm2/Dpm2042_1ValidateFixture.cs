using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Distribution.Access.Dpm20;
using EbaDpm.Converter.Distribution.Tests.Schema;

namespace EbaDpm.Converter.Distribution.Tests.Dpm2;

/// <summary>
/// The same idea as <see cref="Dpm2043ValidateFixture"/> applied to the 4.2.1 publication
/// (<c>Data/DPM2 Database_v 4_2_1.accdb</c>) converted with <c>--release 4.2.1</c>, the LAST release
/// the database declares (a reader opened without a requested cutoff resolves it as well, which
/// <see cref="DerivedCutoffReleaseCode"/> records). Every other fixture over this file converts
/// <c>--release 4.2</c>.
///
/// It has its own collection so it does not compete with <c>Dpm2Skeleton</c>/<c>Dpm2</c>. It runs
/// the conversion ONCE through the real CLI and clears the SQLite connection pool before leaving the
/// constructor because <see cref="EbaDpm.Converter.Core.Validation.Validator.Run"/> needs exclusive
/// <c>File.OpenRead</c> access to the same file for the SHA-256. When the data directory is not
/// available the constructor does nothing (the tests are skipped).
/// </summary>
public sealed class Dpm2042_1ValidateFixture : IDisposable
{
    /// <summary><c>Release.Code</c> of the 4.2.1 release, the last one the database declares.</summary>
    public const string Release421Code = "4.2.1";

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

        TempDirectory = Path.Join(Path.GetTempPath(), $"EbaDpm.Dpm2042_1ValidateSuiteTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDirectory);
        GeneratedDatabasePath = Path.Join(TempDirectory, "generated-dpm2042_1-validate.db");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        ExitCode = CliRunner.Run(
            ["--source", RepoPaths.AccessDpm20DatabasePath, "--release", Release421Code, "--output", GeneratedDatabasePath],
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
