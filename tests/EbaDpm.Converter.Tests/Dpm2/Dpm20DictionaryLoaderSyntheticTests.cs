using EbaDpm.Converter.Core.Mapping.Dpm20;
using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Synthetic tests of <see cref="Dpm20DictionaryLoader.Load"/> through <see cref="InMemoryDictionarySource"/>:
/// no Access, no data files. Rule under test: docs/mapping-dpm2.md, section "mDomain", `DataType`.
/// </summary>
public sealed class Dpm20DictionaryLoaderSyntheticTests : IDisposable
{
    private readonly string _tempDirectory;

    public Dpm20DictionaryLoaderSyntheticTests()
    {
        _tempDirectory = Path.Join(Path.GetTempPath(), $"EbaDpm.DictSynth_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort.
        }
    }

    [Fact]
    public void TypedDomainDataType_IsTheSingleTypeOfItsProperties_AndNullWhenUnresolved()
    {
        var source = new InMemoryDictionarySource();
        source.DataTypes.AddRange([new(1, "es"), new(2, "s"), new(3, "i"), new(4, "d")]);

        // Structural categories required by the loader, then four domains.
        source.Categories.AddRange(
        [
            new(1, "_PR", "Properties", null, true),
            new(2, "_TE", "Templates", null, true),
            new(3, "_NA", "Not applicable", null, false),
            new(10, "TXT", "Text domain", null, false),    // typed, only 'es'
            new(11, "NES", "Non-empty string", null, false), // typed, only 's'
            new(12, "MIX", "Mixed types", null, false),    // typed, 'i' and 'd'
            new(13, "NOP", "No property", null, false),    // typed, no property
            new(14, "ENU", "Enumerated", null, true),      // enumerated
        ]);

        // Properties 101..104 and their data types; 105 shares the TXT domain with 101.
        source.Properties.AddRange(
        [
            new(101, 1), new(102, 2), new(103, 3), new(104, 4), new(105, 1),
        ]);
        source.PropertyCategories.AddRange(
        [
            new(101, 10, 1, null),
            new(105, 10, 1, null),
            new(102, 11, 1, null),
            new(103, 12, 1, null),
            new(104, 12, 1, null),
        ]);

        var path = Path.Join(_tempDirectory, "out.db");
        SchemaCreator.Create(path, overwrite: false);

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        Dpm20DictionaryLoader.Load(source, connection, out var diagnostics);

        var dataTypeByCode = ReadDomainDataTypes(connection);

        Assert.Equal("String", dataTypeByCode["TXT"]);
        Assert.Equal("NotEmptyString", dataTypeByCode["NES"]);
        Assert.Null(dataTypeByCode["MIX"]);
        Assert.Null(dataTypeByCode["NOP"]);
        Assert.Null(dataTypeByCode["ENU"]);

        // The two unresolved typed domains are named; nothing else is.
        Assert.Equal(2, diagnostics.UnresolvedDomainDataTypeCases.Count);
        Assert.Contains(diagnostics.UnresolvedDomainDataTypeCases, c => c.StartsWith("MIX ", StringComparison.Ordinal));
        Assert.Contains(diagnostics.UnresolvedDomainDataTypeCases, c => c.StartsWith("NOP ", StringComparison.Ordinal));
    }

    private static Dictionary<string, string?> ReadDomainDataTypes(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DomainCode, DataType FROM mDomain";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        return result;
    }
}
