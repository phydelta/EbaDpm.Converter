using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Distribution.Tests.Schema;

namespace EbaDpm.Converter.Distribution.Tests.Dpm2;

/// <summary>
/// The 4.4 publication (#67): <c>Data/DPM2 Database_v 4_4_20261006.accdb</c> converted ONCE with
/// <c>--release 4.4</c> through the real CLI, and the EBA Annotated Table Layouts 4.4 extracted ONCE
/// with that same SQLite file as <c>--dictionary</c> (the 4.4 layout file names carry no framework:
/// it is resolved from the module code through the dictionary). Own collection, like
/// <see cref="Dpm2043ValidateFixture"/>, so it does not compete for memory or disk with the others.
///
/// The SQLite connection pool is cleared before leaving each step because
/// <see cref="EbaDpm.Converter.Core.Validation.Validator.Run"/> needs exclusive <c>File.OpenRead</c>
/// access to the file for the SHA-256. When the data directory is not available the constructor does
/// nothing (the tests are skipped).
/// </summary>
public sealed class Dpm2044ValidateFixture : IDisposable
{
    /// <summary><c>Release.Code</c> of the 4.4 release, the last one the 4.4 database declares.</summary>
    public const string Release44Code = RepoPaths.Release44Code;

    public string TempDirectory { get; } = null!;

    public string GeneratedDatabasePath { get; } = null!;

    public int ExitCode { get; }

    public string Stdout { get; } = string.Empty;

    public string Stderr { get; } = string.Empty;

    private readonly Lazy<(string Path, LayoutExtractionSummary Summary)> _layouts = null!;

    /// <summary>The 4.4 layout repository, extracted lazily ONCE with <see cref="GeneratedDatabasePath"/> as dictionary.</summary>
    public string Layouts44DatabasePath => _layouts.Value.Path;

    public LayoutExtractionSummary Layouts44Summary => _layouts.Value.Summary;

    public Dpm2044ValidateFixture()
    {
        if (!RepoPaths.IsDataAvailable)
        {
            return;
        }

        RepoPaths.EnsureAccessDpm20Release44DatabaseExists();

        TempDirectory = Path.Join(Path.GetTempPath(), $"EbaDpm.Dpm2044ValidateSuiteTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDirectory);
        GeneratedDatabasePath = Path.Join(TempDirectory, "generated-dpm2044-validate.db");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        ExitCode = CliRunner.Run(
            ["--source", RepoPaths.AccessDpm20Release44DatabasePath, "--release", Release44Code, "--output", GeneratedDatabasePath],
            stdout,
            stderr);
        Stdout = stdout.ToString();
        Stderr = stderr.ToString();

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        _layouts = new Lazy<(string, LayoutExtractionSummary)>(ExtractLayouts);
    }

    private (string Path, LayoutExtractionSummary Summary) ExtractLayouts()
    {
        RepoPaths.EnsureTableLayouts44DirectoryExists();
        var path = Path.Join(TempDirectory, "layouts44.db");
        var summary = LayoutExtractor.Extract(RepoPaths.TableLayouts44Directory, path, overwrite: false, () => DistributionLayoutDictionaryLoader.Load(GeneratedDatabasePath));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return (path, summary);
    }

    public void Dispose()
    {
        try
        {
            if (TempDirectory is not null && Directory.Exists(TempDirectory))
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(TempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort: a late SQLite handle must not bring down the test suite.
        }
    }
}

[CollectionDefinition("Dpm2044ValidateSuite")]
public sealed class Dpm2044ValidateCollection : ICollectionFixture<Dpm2044ValidateFixture>
{
}
