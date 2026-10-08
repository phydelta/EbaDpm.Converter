using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Collection fixture: runs ONCE, through the real CLI (<see cref="CliRunner.Run"/>, exactly the
/// path a user follows with <c>--all</c>), the conversion of the complete DPM 2.0 skeleton over
/// <c>Data/DPM2 Database_v 4_2_20251125.accdb</c>, and leaves the resulting <c>.db</c> open in
/// read-only mode so that the count and column-by-column tests query the same file, without
/// reopening the 755 MB Access database for every test.
///
/// The skeleton conversion is "cheap" (skeleton only): it does not need its own collection for
/// memory reasons, but it does live in its own xUnit collection (distinct from "Dpm2") so that it
/// does not compete for the same 755 MB Access database at the same time as
/// <see cref="Dpm20AccessReaderFixture"/> if xUnit decides to parallelise collections.
///
/// This fixture also covers what used to be a separate validation fixture: both ran the SAME
/// <c>--all</c> conversion through the real CLI, one to leave the connection OPEN (fast queries)
/// and the other to leave the file FREE of any connection (<c>Validator.Run</c>). Here a single
/// conversion is made and the file is copied to <see cref="ValidatedDatabasePath"/> at the moment
/// when no connection is open (right after <c>CliRunner.Run</c> + <c>ClearAllPools</c>, before
/// opening <see cref="GeneratedConnection"/>) -- the same pattern <c>AllFixture</c> uses for DPM 1.0.
///
/// When the EBA data files are not available the constructor does nothing and the members keep
/// their default values; all tests that use this fixture are data tests and are skipped.
/// </summary>
public sealed class Dpm20SkeletonFixture : IDisposable
{
    public string TempDirectory { get; } = null!;
    public string GeneratedDatabasePath { get; } = null!;

    /// <summary>
    /// Copy of the same <c>--all</c> run, taken with NO open connection on top of it -- exactly what
    /// a second full conversion would have produced.
    /// </summary>
    public string ValidatedDatabasePath { get; } = null!;

    public int ExitCode { get; }
    public string Stdout { get; } = null!;
    public string Stderr { get; } = null!;

    public SqliteConnection GeneratedConnection { get; } = null!;

    public Dpm20SkeletonFixture()
    {
        if (!RepoPaths.IsDataAvailable)
        {
            return;
        }

        RepoPaths.EnsureAccessDpm20DatabaseExists();

        TempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.Dpm20SkeletonTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDirectory);
        GeneratedDatabasePath = Path.Combine(TempDirectory, "generated-dpm20-skeleton.db");

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        ExitCode = CliRunner.Run(
            ["--source", RepoPaths.AccessDpm20DatabasePath, "--output", GeneratedDatabasePath, "--all"],
            stdout,
            stderr);

        Stdout = stdout.ToString();
        Stderr = stderr.ToString();

        // The Microsoft.Data.Sqlite pool may retain the native handle even though CliRunner has
        // already closed its own connection (Dispose does not release the file). It is cleared
        // BEFORE copying so that the copy really is the only interaction with the file at that moment.
        SqliteConnection.ClearAllPools();

        ValidatedDatabasePath = Path.Combine(TempDirectory, "generated-dpm20-skeleton-validated.db");
        File.Copy(GeneratedDatabasePath, ValidatedDatabasePath, overwrite: true);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = GeneratedDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        GeneratedConnection = new SqliteConnection(connectionString);
        GeneratedConnection.Open();
    }

    public void Dispose()
    {
        GeneratedConnection?.Dispose();
        SqliteConnection.ClearAllPools();

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

/// <summary>
/// xUnit collection definition for <see cref="Dpm20SkeletonFixture"/> -- separate from "Dpm2" so
/// that both reads of the 755 MB DPM 2.0 Access database do not compete if xUnit parallelises
/// collections.
/// </summary>
[CollectionDefinition("Dpm2Skeleton")]
public sealed class Dpm2SkeletonCollection : ICollectionFixture<Dpm20SkeletonFixture>
{
}
