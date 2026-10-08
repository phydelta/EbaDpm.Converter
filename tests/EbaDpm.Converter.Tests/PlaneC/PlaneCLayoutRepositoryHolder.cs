using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// The 4.2 layout repository, extracted ONCE for the whole test process and shared between the
/// THREE collections that need to compare it against a real output
/// (<c>Dpm2ValidateSuite</c>, <c>All</c> -- DPM 1.0 --, <c>Dpm2043ValidateSuite</c>):
/// extracting it once per collection would triple the cost (51 .xlsx files) for nothing, because
/// it is read-only and <c>xunit.runner.json</c> sets <c>parallelizeTestCollections=false</c> for
/// the whole project -- there is no possible race between the collections that query it.
///
/// Deliberately NOT an <c>ICollectionFixture</c>: those are tied to ONE collection, and three of
/// them need this repository. A static <c>Lazy&lt;T&gt;</c> is the right piece for "once, shared,
/// with no single owner".
/// </summary>
internal static class PlaneCLayoutRepositoryHolder
{
    private static readonly Lazy<string> LazyPath = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string LayoutsDatabasePath => LazyPath.Value;

    private static string Build()
    {
        RepoPaths.EnsureTableLayouts42DirectoryExists();
        RepoPaths.EnsureReferenceDatabaseExists();

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.PlaneCLayouts_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var path = Path.Combine(tempDirectory, "layouts42.db");

        LayoutExtractor.Extract(RepoPaths.TableLayouts42Directory, path, overwrite: false, RepoPaths.ReferenceDatabasePath);

        // Same reason as in the other fixtures: the Microsoft.Data.Sqlite connection pool retains
        // the native handle even though each individual connection has been closed, and
        // Validator.Run needs to open this same file read-only repeatedly.
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
