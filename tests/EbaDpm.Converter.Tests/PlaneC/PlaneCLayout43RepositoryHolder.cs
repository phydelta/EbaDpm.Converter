using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// The 4.3 layout repository, extracted ONCE for the whole test process, built exactly like
/// <see cref="PlaneCLayoutRepositoryHolder"/> builds the 4.2 one (same extractor, same dictionary,
/// same temporary-directory lifetime), for the plane C checks that compare the DPM 2.0 4.3 output
/// with the 4.3 layouts (C-MHR-01 / C-MHR-02, issue 61).
/// </summary>
internal static class PlaneCLayout43RepositoryHolder
{
    private static readonly Lazy<string> LazyPath = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string LayoutsDatabasePath => LazyPath.Value;

    private static string Build()
    {
        RepoPaths.EnsureTableLayouts43DirectoryExists();
        RepoPaths.EnsureReferenceDatabaseExists();

        var tempDirectory = Path.Join(Path.GetTempPath(), $"EbaDpm.PlaneCLayouts43_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var path = Path.Join(tempDirectory, "layouts43.db");

        LayoutExtractor.Extract(RepoPaths.TableLayouts43Directory, path, overwrite: false, RepoPaths.ReferenceDatabasePath);

        SqliteConnection.ClearAllPools();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteDirectory(tempDirectory);

        return path;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort: a late handle must not prevent the test process from exiting.
        }
    }
}
