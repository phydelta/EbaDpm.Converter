using System.IO;

namespace EbaDpm.Converter.Tests.Schema;

/// <summary>
/// Locates repository paths (root, data directory, reference databases) starting from the test
/// output directory, without assuming a particular working directory.
/// </summary>
/// <remarks>
/// The data directory is <c>EBADPM_TEST_DATA</c> when that environment variable is set, otherwise
/// <c>Data/</c> under the repository root. See <c>docs/test-data.md</c>.
/// </remarks>
internal static class RepoPaths
{
    /// <summary>Name of the environment variable that overrides the data directory.</summary>
    public const string DataDirectoryEnvironmentVariable = "EBADPM_TEST_DATA";

    /// <summary>File name of the 4.2 reference database (schema contract and validation reference).</summary>
    public const string ReferenceDatabaseFileName = "EBA_4.2_Hotfix.db";

    /// <summary>File name of the 4.0 validation reference (second priority).</summary>
    public const string Reference40FileName = "EBA_4.0_ERRATA_5.db";

    /// <summary>
    /// File name of the primary validation reference: 1:1 coverage with the Access database and
    /// pure DPM 1.0.
    /// </summary>
    public const string Reference32FileName = "EBA_3.2_phase_1.db";

    /// <summary>File name of the DPM 1.0 source Access database, release 4.1.</summary>
    public const string AccessDatabaseFileName = "DPM 1.0 Database_v4_1_20250709.accdb";

    /// <summary>
    /// File name of the DPM 2.0 Access database published as 4.2.1 (its [Release] declares 4.2 and
    /// 4.2.1; natural cutoff "4.2.1"). The tests that mean "the 4.2 release" read it with the
    /// explicit cutoff <see cref="Cutoff42ReleaseCode"/>; the natural cutoff is exercised by
    /// <c>Dpm2042_1ValidateFixture</c>. Only read by the tests of the DPM 2.0 pipeline.
    /// </summary>
    public const string AccessDpm20DatabaseFileName = "DPM2 Database_v 4_2_1.accdb";

    /// <summary>
    /// <c>Release.Code</c> of the 4.2 release: the explicit cutoff (<c>--cutoff-release 4.2</c> /
    /// <c>cutoffReleaseCode: "4.2"</c>) with which the tests that mean "the 4.2 release" read
    /// <see cref="AccessDpm20DatabaseFileName"/>.
    /// </summary>
    public const string Cutoff42ReleaseCode = "4.2";

    /// <summary>
    /// File name of the DPM 2.0 Access database of the 4.3 publication. Its cut-off release is
    /// derived by <c>Dpm20AccessReader.ResolveCutoffRelease</c> (Max(ReleaseID) = 1.010.000.050 =
    /// "4.3"), unlike the 4.2.1 file (which derives "4.2.1"). Only read by the tests that exercise
    /// the later publication.
    /// </summary>
    public const string AccessDpm20Release43DatabaseFileName = "DPM2 Database_v 4_3_20260622.accdb";

    /// <summary>
    /// Walks up from the test execution directory until it finds <c>EbaDpm.Converter.sln</c>,
    /// which marks the repository root.
    /// </summary>
    public static string RepositoryRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "EbaDpm.Converter.sln")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException(
                "Could not locate the repository root (EbaDpm.Converter.sln) " +
                $"walking up from '{AppContext.BaseDirectory}'.");
        }
    }

    /// <summary>
    /// The data directory: the value of <c>EBADPM_TEST_DATA</c> when set, otherwise
    /// <c>&lt;repository root&gt;/Data</c>.
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
            return string.IsNullOrWhiteSpace(fromEnvironment)
                ? Path.Combine(RepositoryRoot, "Data")
                : fromEnvironment;
        }
    }

    /// <summary>
    /// Whether the data directory exists. The data-dependent tests are skipped when it does not
    /// (see <see cref="DataFactAttribute"/>).
    /// </summary>
    public static bool IsDataAvailable => Directory.Exists(DataDirectory);

    public static string ReferenceDatabasePath => Path.Combine(DataDirectory, ReferenceDatabaseFileName);

    public static string Reference40Path => Path.Combine(DataDirectory, Reference40FileName);

    public static string Reference32Path => Path.Combine(DataDirectory, Reference32FileName);

    public static string AccessDatabasePath => Path.Combine(DataDirectory, AccessDatabaseFileName);

    public static string AccessDpm20DatabasePath => Path.Combine(DataDirectory, AccessDpm20DatabaseFileName);

    public static string AccessDpm20Release43DatabasePath => Path.Combine(DataDirectory, AccessDpm20Release43DatabaseFileName);

    /// <summary>Directory with the 4.2 Annotated Table Layouts.</summary>
    public static string TableLayouts42Directory => Path.Combine(DataDirectory, "4.2 table layouts");

    /// <summary>Directory with the 4.3 Annotated Table Layouts.</summary>
    public static string TableLayouts43Directory => Path.Combine(DataDirectory, "4.3 table layouts");

    /// <summary>
    /// Directory with the 3.2 Annotated Table Layouts: the DPM 1.0 era, with no date or module in
    /// the file name (<c>LayoutFileNameParser</c>). Only 14 files, much cheaper to extract than
    /// the 4.2 corpus (51 files), and the corpus with which check C-DPS-01 started to monitor
    /// DPM 1.0.
    /// </summary>
    public static string TableLayouts32Directory => Path.Combine(DataDirectory, "3.2 table layouts");

    /// <summary>Verifies that the 3.2 layouts directory exists.</summary>
    public static void EnsureTableLayouts32DirectoryExists()
    {
        if (!Directory.Exists(TableLayouts32Directory))
        {
            throw new InvalidOperationException(
                $"Directory '{TableLayouts32Directory}' does not exist. These tests " +
                "require the 3.2 Annotated Table Layouts and cannot run without them. " +
                "See docs/test-data.md.");
        }
    }

    /// <summary>Verifies that the 4.2 layouts directory exists.</summary>
    public static void EnsureTableLayouts42DirectoryExists()
    {
        if (!Directory.Exists(TableLayouts42Directory))
        {
            throw new InvalidOperationException(
                $"Directory '{TableLayouts42Directory}' does not exist. These tests " +
                "require the 4.2 Annotated Table Layouts and cannot run without them. " +
                "See docs/test-data.md.");
        }
    }

    /// <summary>Verifies that the 4.3 layouts directory exists.</summary>
    public static void EnsureTableLayouts43DirectoryExists()
    {
        if (!Directory.Exists(TableLayouts43Directory))
        {
            throw new InvalidOperationException(
                $"Directory '{TableLayouts43Directory}' does not exist. These tests " +
                "require the 4.3 Annotated Table Layouts and cannot run without them. " +
                "See docs/test-data.md.");
        }
    }

    /// <summary>
    /// Verifies that the 4.2 reference database exists. If not, it throws an actionable message
    /// instead of letting the test be silently skipped.
    /// </summary>
    public static void EnsureReferenceDatabaseExists() => EnsureFileExists(
        ReferenceDatabasePath, $"the reference database '{ReferenceDatabaseFileName}'");

    /// <summary>Verifies that the 4.0 reference (second priority) exists.</summary>
    public static void EnsureReference40DatabaseExists() => EnsureFileExists(
        Reference40Path, $"the reference database '{Reference40FileName}'");

    /// <summary>Verifies that the primary 3.2 reference exists.</summary>
    public static void EnsureReference32DatabaseExists() => EnsureFileExists(
        Reference32Path, $"the primary reference database '{Reference32FileName}'");

    /// <summary>Verifies that the source Access database (DPM 1.0, release 4.1) exists.</summary>
    public static void EnsureAccessDatabaseExists() => EnsureFileExists(
        AccessDatabasePath, $"the source Access database '{AccessDatabaseFileName}'");

    /// <summary>Verifies that the DPM 2.0 Access database exists.</summary>
    public static void EnsureAccessDpm20DatabaseExists() => EnsureFileExists(
        AccessDpm20DatabasePath, $"the DPM 2.0 Access database '{AccessDpm20DatabaseFileName}'");

    /// <summary>Verifies that the DPM 2.0 Access database of the 4.3 publication exists.</summary>
    public static void EnsureAccessDpm20Release43DatabaseExists() => EnsureFileExists(
        AccessDpm20Release43DatabasePath, $"the DPM 2.0 4.3 Access database '{AccessDpm20Release43DatabaseFileName}'");

    private static void EnsureFileExists(string path, string description)
    {
        if (!Directory.Exists(DataDirectory))
        {
            throw new InvalidOperationException(
                $"The data directory was not found at '{DataDirectory}'. " +
                $"The tests require {description} and cannot run without it. " +
                "Set EBADPM_TEST_DATA or create ./Data; see docs/test-data.md.");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"File '{path}' does not exist. The tests require {description} and cannot " +
                "run without it. See docs/test-data.md.");
        }
    }
}
