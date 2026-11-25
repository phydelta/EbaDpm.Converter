using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Native.Tests.Support;
using EbaDpm.Converter.Native.Validation;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.Validation;

/// <summary>
/// <see cref="NativeOutputDetector"/>: a native output is recognised from the catalogue of the SQLite
/// file (tables <c>Category</c> and <c>TableVersionCell</c>, no <c>mModule</c>), never from data.
/// </summary>
public sealed class NativeOutputDetectorTests : IDisposable
{
    private readonly string _scratch = Path.Join(Path.GetTempPath(), $"native-detect_{Environment.ProcessId}_{Guid.NewGuid():N}");

    public NativeOutputDetectorTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
            // Left for the OS temp cleanup.
        }
    }

    private static SqliteConnection Create(string path, bool mustExist = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mustExist ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private string CreateSqlite(string name, params string[] tables)
    {
        var path = Path.Join(_scratch, name);
        using (var connection = Create(path))
        {
            foreach (var table in tables)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"CREATE TABLE \"{table}\" (\"Id\" INTEGER)";
                command.ExecuteNonQuery();
            }
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    private static int Cli(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            return CliRunner.Run(args, stdout, stderr);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void NativeSchemaOnlyFile_IsNative()
    {
        var path = Path.Join(_scratch, "native-schema.db");
        Assert.Equal(0, Cli("--schema-only", "--target", "native", "--output", path));

        Assert.True(NativeOutputDetector.IsNativeOutput(path));
    }

    [Fact]
    public void DistributionSchemaOnlyFile_IsNotNative()
    {
        var path = Path.Join(_scratch, "dist-schema.db");
        Assert.Equal(0, Cli("--schema-only", "--output", path));

        Assert.False(NativeOutputDetector.IsNativeOutput(path));
    }

    [Fact]
    public void FileWithCategoryTableVersionCellAndMModule_IsNotNative()
    {
        Assert.False(NativeOutputDetector.IsNativeOutput(CreateSqlite("mixed.db", "Category", "TableVersionCell", "mModule")));
    }

    [Fact]
    public void FileWithCategoryAndTableVersionCellOnly_IsNative()
    {
        Assert.True(NativeOutputDetector.IsNativeOutput(CreateSqlite("minimal.db", "Category", "TableVersionCell")));
    }

    [Theory]
    [InlineData("Category")]
    [InlineData("TableVersionCell")]
    [InlineData("mModule")]
    public void FileWithOnlyOneOfTheMarkerTables_IsNotNative(string table)
    {
        Assert.False(NativeOutputDetector.IsNativeOutput(CreateSqlite("one.db", table)));
    }

    [Fact]
    public void UnrelatedDatabase_IsNotNative()
    {
        Assert.False(NativeOutputDetector.IsNativeOutput(CreateSqlite("other.db", "Other")));
    }

    [Fact]
    public void Detection_LooksAtTheCatalogue_NotAtTheRows()
    {
        // A distribution-looking file whose data mentions the native table names is still not native.
        var path = CreateSqlite("data.db", "mModule", "Notes");
        using (var connection = Create(path, mustExist: true))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO \"Notes\" (\"Id\") VALUES (1)";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        Assert.False(NativeOutputDetector.IsNativeOutput(path));
    }

    [Fact]
    public void BlankPath_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => NativeOutputDetector.IsNativeOutput(" "));
    }
}

/// <summary>The detector on a real conversion output (needs ACE).</summary>
public sealed class NativeOutputDetectorConvertedTests(NativeValidationFixture fixture) : IClassFixture<NativeValidationFixture>
{
    [AceFact]
    public void ConvertedNativeOutput_IsNative() => Assert.True(NativeOutputDetector.IsNativeOutput(fixture.OutputPath));
}
