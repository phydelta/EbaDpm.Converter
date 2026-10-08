using System.Data.OleDb;
using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Tests.Schema;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Source model autodetection of a <c>.accdb</c> by CATALOG (<c>GetSchema("Tables")</c>), never by data.
/// </summary>
public sealed class SourceModelDetectorTests
{
    [DataFact]
    public void Dpm20Accdb_IsDetectedAsDpm20()
    {
        RepoPaths.EnsureAccessDpm20DatabaseExists();

        var model = SourceModelDetector.Detect(RepoPaths.AccessDpm20DatabasePath);

        Assert.Equal(DpmSourceModel.Dpm20, model);
    }

    [DataFact]
    public void Dpm10Accdb_IsDetectedAsDpm10()
    {
        RepoPaths.EnsureAccessDatabaseExists();

        var model = SourceModelDetector.Detect(RepoPaths.AccessDatabasePath);

        Assert.Equal(DpmSourceModel.Dpm10, model);
    }

    /// <summary>
    /// Documented rule (docs/source-models.md): a table named <c>Domain</c> means DPM 1.0, and it
    /// wins even when <c>Category</c> also exists (the old-schema DPM 1.0 files have both). If the
    /// detector looked only at <c>Category</c>, such a file would be silently misclassified.
    /// </summary>
    [AceFact]
    public void SyntheticAccdb_DomainAndCategory_IsDpm10() =>
        AssertDetected(DpmSourceModel.Dpm10, "Domain", "Category");

    [AceFact]
    public void SyntheticAccdb_OnlyDomain_IsDpm10() =>
        AssertDetected(DpmSourceModel.Dpm10, "Domain");

    [AceFact]
    public void SyntheticAccdb_OnlyCategory_IsDpm20() =>
        AssertDetected(DpmSourceModel.Dpm20, "Category");

    private static void AssertDetected(DpmSourceModel expected, params string[] tables)
    {
        var tempPath = Dpm2TestAccdbFactory.CreateAccdbWithTables(tables);
        try
        {
            Assert.Equal(expected, SourceModelDetector.Detect(tempPath));
        }
        finally
        {
            OleDbConnection.ReleaseObjectPool();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// A file that is neither DPM model must produce an EXPLICIT exception
    /// (<see cref="SourceModelDetectionException"/>), never a guess. It is provoked with a real,
    /// empty <c>.accdb</c> (no user tables, created with ADOX) that opens fine through ACE OLEDB
    /// but has neither <c>Domain</c> nor <c>Category</c> -- the "neither of the two" case.
    /// </summary>
    [AceFact]
    public void FileWithNeitherModel_ThrowsExplicitException_NamingFileAndExpectation()
    {
        var tempPath = Dpm2TestAccdbFactory.CreateEmptyAccdb();
        try
        {
            var exception = Assert.Throws<SourceModelDetectionException>(
                () => SourceModelDetector.Detect(tempPath));

            // It names the file...
            Assert.Contains(tempPath, exception.Message, StringComparison.Ordinal);
            // ...and says what was expected (the two model markers).
            Assert.Contains("Domain", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Category", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            // ACE OLEDB keeps the file open in its connection pool (provider behaviour, not the
            // test's); it has to be released before the temporary file can be deleted.
            OleDbConnection.ReleaseObjectPool();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// A file that is not even a valid <c>.accdb</c> (empty, 0 bytes) must fail without assuming any
    /// model. It is a way to provoke the explicit exception (a temporary empty file is enough).
    ///
    /// MEASURED: it does not satisfy the literal requirement "the message names the file". ACE OLEDB
    /// rejects the connection BEFORE reaching the catalog with an <c>OleDbException</c> whose text
    /// is a localized "database format not recognized ''." -- note the empty quotes: the provider
    /// does not interpolate the path into its own message, and <see cref="SourceModelDetector.Detect"/>
    /// does not wrap the open failure with file context (it only wraps the "opens fine, catalog
    /// without any marker" case in <see cref="SourceModelDetectionException"/>). The assertion is
    /// kept STRICT so that the failure stays documented instead of silenced.
    /// </summary>
    [AceFact]
    public void EmptyFile_ThrowsWithMessageNamingTheFile()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"empty-file_{Environment.ProcessId}_{Guid.NewGuid():N}.accdb");
        File.WriteAllBytes(tempPath, []);
        try
        {
            var exception = Assert.ThrowsAny<Exception>(() => SourceModelDetector.Detect(tempPath));

            // Never a guess: it is at least thrown and no invented DpmSourceModel is returned.
            Assert.False(exception is null);

            // Literal requirement: "check that the message names the file".
            Assert.Contains(tempPath, exception!.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
