using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// The 3.2 layout repository, extracted ONCE for the whole test process, same pattern as
/// <see cref="PlaneCLayoutRepositoryHolder"/> but over <c>Data/3.2 table layouts</c> -- the corpus
/// with which <c>C-DPS-01</c> WATCHES DPM 1.0 instead of just measuring it (472 tables / 86,115
/// signatures / 0 violations). Only 14 files (versus 51 for 4.2): cheap to extract, there is no
/// need to share it between different xUnit collections -- a static <c>Lazy&lt;T&gt;</c> is enough
/// so that the several <c>[Fact]</c>s of one file do not extract it twice.
/// </summary>
internal static class PlaneCLayout32RepositoryHolder
{
    private static readonly Lazy<string> LazyPath = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string LayoutsDatabasePath => LazyPath.Value;

    private static string Build()
    {
        RepoPaths.EnsureTableLayouts32DirectoryExists();
        RepoPaths.EnsureReferenceDatabaseExists();

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.PlaneCLayouts32_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var path = Path.Combine(tempDirectory, "layouts32.db");

        // Same dictionary (4.2 Hotfix) that PlaneCLayoutRepositoryHolder uses for 4.2: the
        // dictionary only resolves whether the code of a loose term is a MET member or a dimension
        // (LayoutDictionary); it is not tied to a specific release.
        LayoutExtractor.Extract(RepoPaths.TableLayouts32Directory, path, overwrite: false, RepoPaths.ReferenceDatabasePath);

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
