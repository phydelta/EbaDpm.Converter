using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// Collection fixture: runs ONCE, for the whole "LayoutsRealData" collection, the real extraction
/// of the EBA Annotated Table Layouts 4.2 (with and without <c>--dictionary</c>) and 4.3 (with
/// dictionary), over the REAL files in <c>Data/4.2 table layouts</c> and
/// <c>Data/4.3 table layouts</c>. It contrasts the reported figures against an independent
/// derivation.
///
/// The data directory is read-only: this fixture only READS from it; the output repository lives
/// in <c>%TEMP%</c>, like <see cref="EbaDpm.Converter.Tests.All.AllFixture"/>.
///
/// When the data directory is not available the fixture does nothing (members are left as
/// <c>null</c>), so that the skipped data tests can still be constructed.
/// </summary>
public sealed class LayoutRepositoryFixture : IDisposable
{
    public string TempDirectory { get; }

    public LayoutExtractionSummary Summary42WithDictionary { get; }
    public SqliteConnection Repository42WithDictionary { get; }

    public LayoutExtractionSummary Summary42NoDictionary { get; }
    public SqliteConnection Repository42NoDictionary { get; }

    public LayoutExtractionSummary Summary43WithDictionary { get; }
    public SqliteConnection Repository43WithDictionary { get; }

    public LayoutRepositoryFixture()
    {
        if (!RepoPaths.IsDataAvailable)
        {
            TempDirectory = null!;
            Summary42WithDictionary = null!;
            Repository42WithDictionary = null!;
            Summary42NoDictionary = null!;
            Repository42NoDictionary = null!;
            Summary43WithDictionary = null!;
            Repository43WithDictionary = null!;
            return;
        }

        RepoPaths.EnsureTableLayouts42DirectoryExists();
        RepoPaths.EnsureTableLayouts43DirectoryExists();
        RepoPaths.EnsureReferenceDatabaseExists();

        TempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.LayoutTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDirectory);

        var path42Dict = Path.Combine(TempDirectory, "layouts42_dict.db");
        Summary42WithDictionary = LayoutExtractor.Extract(
            RepoPaths.TableLayouts42Directory, path42Dict, overwrite: false, RepoPaths.ReferenceDatabasePath);
        Repository42WithDictionary = OpenReadOnly(path42Dict);

        var path42NoDict = Path.Combine(TempDirectory, "layouts42_nodict.db");
        Summary42NoDictionary = LayoutExtractor.Extract(
            RepoPaths.TableLayouts42Directory, path42NoDict, overwrite: false, dictionaryPath: null);
        Repository42NoDictionary = OpenReadOnly(path42NoDict);

        var path43Dict = Path.Combine(TempDirectory, "layouts43_dict.db");
        Summary43WithDictionary = LayoutExtractor.Extract(
            RepoPaths.TableLayouts43Directory, path43Dict, overwrite: false, RepoPaths.ReferenceDatabasePath);
        Repository43WithDictionary = OpenReadOnly(path43Dict);
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

    public void Dispose()
    {
        Repository42WithDictionary?.Dispose();
        Repository42NoDictionary?.Dispose();
        Repository43WithDictionary?.Dispose();
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
            // Best-effort: a late SQLite handle must not bring the suite down.
        }
    }
}

/// <summary>xUnit collection that avoids repeating three full extractions (51+4 files, ~1,200
/// sheets) for every layout verification test class.</summary>
[CollectionDefinition("LayoutsRealData")]
public sealed class LayoutsRealDataCollection : ICollectionFixture<LayoutRepositoryFixture>
{
}
