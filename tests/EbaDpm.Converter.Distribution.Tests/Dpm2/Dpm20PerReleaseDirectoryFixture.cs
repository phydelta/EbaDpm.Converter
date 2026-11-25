using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Distribution.Tests.Infrastructure;
using EbaDpm.Converter.Distribution.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Distribution.Tests.Dpm2;

/// <summary>
/// Runs ONCE, through the real CLI, the DIRECTORY mode of #73 over the 4.2.1 database
/// (<c>--source ... --output &lt;directory&gt;</c>, no <c>--release</c>): one <c>dpm-&lt;release&gt;.db</c>
/// per release that publishes at least one non-document module version. The release list, the
/// empty releases and the per-release expectations are derived from the Access database by the
/// tests that use this fixture; nothing about this database is hard-coded here.
///
/// When the EBA data files are not available the constructor does nothing and the members keep
/// their default values; every test that uses it is a data test and is skipped.
/// </summary>
public sealed class Dpm20PerReleaseDirectoryFixture : IDisposable
{
    public string TempDirectory { get; } = null!;

    /// <summary>The directory that received the files.</summary>
    public string OutputDirectory { get; } = null!;

    public int ExitCode { get; }

    public string Stdout { get; } = null!;

    public string Stderr { get; } = null!;

    public Dpm20PerReleaseDirectoryFixture()
    {
        if (!RepoPaths.IsDataAvailable)
        {
            return;
        }

        RepoPaths.EnsureAccessDpm20DatabaseExists();

        TempDirectory = Path.Join(Path.GetTempPath(), $"EbaDpm.Dpm20PerRelease_{Environment.ProcessId}_{Guid.NewGuid():N}");
        OutputDirectory = Path.Join(TempDirectory, "per-release");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        // The directory does not exist yet: the CLI creates it.
        ExitCode = CliRunner.Run(
            ["--source", RepoPaths.AccessDpm20DatabasePath, "--output", OutputDirectory], stdout, stderr);

        Stdout = stdout.ToString();
        Stderr = stderr.ToString();

        SqliteConnection.ClearAllPools();
    }

    /// <summary>The release catalog derived straight from Access: (Code, ReleaseID, non-document module versions starting in it).</summary>
    public static List<(string Code, int Id, long Exportable)> ReleasesFromAccess()
    {
        var accdb = RepoPaths.AccessDpm20DatabasePath;
        return AccessCensus.IntByCode(accdb, "SELECT [Code], [ReleaseID] FROM [Release]")
            .OrderBy(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value, AccessCensus.Scalar(
                accdb,
                "SELECT COUNT(1) FROM [ModuleVersion] mv INNER JOIN [Module] m ON m.[ModuleID] = mv.[ModuleID] "
                + $"WHERE mv.[StartReleaseID] = {kv.Value} AND m.[isDocumentModule] = 0")))
            .ToList();
    }

    public string FileOf(string releaseCode) => Path.Join(OutputDirectory, $"dpm-{releaseCode}.db");

    public void Dispose()
    {
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

/// <summary>xUnit collection definition for <see cref="Dpm20PerReleaseDirectoryFixture"/>.</summary>
[CollectionDefinition("Dpm2PerReleaseDirectory")]
public sealed class Dpm2PerReleaseDirectoryCollection : ICollectionFixture<Dpm20PerReleaseDirectoryFixture>
{
}
