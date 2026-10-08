using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Tests.Schema;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// The same idea as <see cref="Dpm20SkeletonFixture"/> applied to the 4.3 publication:
/// <c>Data/DPM2 Database_v 4_3_20260622.accdb</c>, whose derived cutoff release
/// (<c>Dpm20AccessReader.ResolveCutoffRelease</c>, Max(ReleaseID)) is "4.3", not "4.2". It has its
/// own collection so it does not compete for memory/disk with <c>Dpm2Skeleton</c>/<c>Dpm2</c>,
/// which read the Access database of the previous publication.
///
/// Unlike <see cref="Dpm20SkeletonFixture"/>, this fixture has no other consumer that needs an
/// open connection on the same file, so it keeps a single <c>GeneratedDatabasePath</c>: it runs
/// <c>--all</c> ONCE through the real CLI, and clears the SQLite connection pool before leaving
/// the constructor because <see cref="EbaDpm.Converter.Core.Validation.Validator.Run"/> needs
/// exclusive <c>File.OpenRead</c> access to the same file for the SHA-256.
/// When the data directory is not available the constructor does nothing (the tests are skipped).
/// </summary>
public sealed class Dpm2043ValidateFixture : IDisposable
{
    public string TempDirectory { get; } = null!;
    public string GeneratedDatabasePath { get; } = null!;
    public int ExitCode { get; }

    public Dpm2043ValidateFixture()
    {
        if (!RepoPaths.IsDataAvailable)
        {
            return;
        }

        RepoPaths.EnsureAccessDpm20Release43DatabaseExists();

        TempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.Dpm2043ValidateSuiteTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDirectory);
        GeneratedDatabasePath = Path.Combine(TempDirectory, "generated-dpm2043-validate.db");

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        ExitCode = CliRunner.Run(
            ["--source", RepoPaths.AccessDpm20Release43DatabasePath, "--output", GeneratedDatabasePath, "--all"],
            stdout,
            stderr);

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

[CollectionDefinition("Dpm2043ValidateSuite")]
public sealed class Dpm2043ValidateCollection : ICollectionFixture<Dpm2043ValidateFixture>
{
}
