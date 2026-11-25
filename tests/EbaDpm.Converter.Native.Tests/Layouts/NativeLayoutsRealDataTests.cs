using EbaDpm.Converter.Native.Tests.PlaneC;
using EbaDpm.Converter.Native.Tests.Support;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.Layouts;

/// <summary>
/// The 4.4 layouts extracted with the dictionary of the native 4.4 output against the same layouts
/// extracted with the dictionary of the 45-table 4.4 output (docs/table-layouts.md, "Dictionary from a
/// native output": identical table by table on the 4.4 database). The native file is the one the
/// shared <see cref="Native44Fixture"/> converted; the 45-table dictionary is the <c>--release 4.4</c>
/// conversion of the same Access database. The comparison itself is shown able to see a difference.
/// </summary>
[Collection(Native44Collection.Name)]
public sealed class NativeLayoutsRealDataTests(Native44Fixture fixture)
{
    [DataFact]
    public void Layouts44_ExtractedWithTheNativeDictionary_AreIdenticalToThoseExtractedWith45TableDictionary()
    {
        RepoPaths.EnsureTableLayouts44DirectoryExists();
        Assert.True(fixture.Converted, "The 4.4 native conversion did not run although the data directory exists (is ACE installed?).");

        var native = RealLayoutRepositories.Extract("layouts-4.4-native-dictionary", RepoPaths.TableLayouts44Directory, fixture.OutputPath);
        var distribution = RealLayoutRepositories.Extract(
            "layouts-4.4-distribution-dictionary", RepoPaths.TableLayouts44Directory, RealLayoutRepositories.Distribution44(fixture.SourcePath));

        var differences = LayoutRepositoryComparer.Differences(native, distribution);

        Assert.True(
            differences.Count == 0,
            "The layout repositories differ (native dictionary against 45-table dictionary):\n" + string.Join("\n", differences));

        // Not vacuous: the repository has content (counted, never pinned: the layouts of a release are data).
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = native, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        foreach (var table in new[] { "LayoutFile", "LayoutSheet", "LayoutDeclaration", "LayoutOrdinate", "LayoutValue", "LayoutCell" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(1) FROM \"{table}\"";
            Assert.True(Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0, $"{table} is empty");
        }
    }

    [DataFact]
    public void Layouts44_ExtractionRecordsTheNativeFileAsItsDictionary()
    {
        RepoPaths.EnsureTableLayouts44DirectoryExists();
        Assert.True(fixture.Converted, "The 4.4 native conversion did not run although the data directory exists (is ACE installed?).");

        var native = RealLayoutRepositories.Extract("layouts-4.4-native-dictionary", RepoPaths.TableLayouts44Directory, fixture.OutputPath);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = native, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DictionaryPath, DictionarySha256 FROM LayoutExtraction";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(fixture.OutputPath, reader.GetString(0));
        Assert.Matches("^[0-9a-fA-F]{64}$", reader.GetString(1));
    }
}
