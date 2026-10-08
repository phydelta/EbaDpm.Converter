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
    /// The 2014 Access database (old schema) is DPM 1.0 with a DIFFERENT schema. It is the concrete
    /// case for which the detector must look at BOTH tables (<c>Domain</c> present AND
    /// <c>Category</c> absent/irrelevant) and not just one: if the detector looked only at
    /// <c>Category</c>, this file could be silently misclassified.
    /// OPTIONAL: if the file is not available in this environment, the test returns without asserting.
    /// </summary>
    [DataFact]
    public void Dpm2014Accdb_IfAvailable_IsDetectedAsDpm10_NotAnException()
    {
        if (!RepoPaths.AccessDpm2014DatabaseExists())
        {
            // The file is not invented: the omission is documented and the test moves on.
            return;
        }

        var model = SourceModelDetector.Detect(RepoPaths.AccessDpm2014DatabasePath);

        Assert.Equal(DpmSourceModel.Dpm10, model);
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
