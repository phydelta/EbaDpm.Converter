using System.Globalization;
using EbaDpm.Converter.Native.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.PlaneC;

/// <summary>
/// A small native DPM 2.0 SQLite file built in the test: the real native schema
/// (<see cref="NativeSchema.CreateEmpty"/>, the 4.4 baseline) filled with a few rows through the
/// helpers below. Columns the test does not name are filled with the neutral value of their type
/// when they are NOT NULL (0 or the empty text), so a test only states what it is about. Foreign
/// keys are not enforced: the rows are a fixture for the readers, not a converted database.
/// IDs are arbitrary and local to one file; the tests compare by code.
/// </summary>
internal sealed class SyntheticNativeFile : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), $"native-plc_{Environment.ProcessId}_{Guid.NewGuid():N}");
    private readonly Dictionary<string, List<(string Name, string Type, bool NotNull)>> _columns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _categoryByCode = new(StringComparer.Ordinal);
    private long _next = 1000;

    public SyntheticNativeFile(bool withBaselineReleases = true)
    {
        Directory.CreateDirectory(_directory);
        FilePath = Path.Join(_directory, "native.db");
        File.Copy(TemplatePath, FilePath);
        Connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString());
        Connection.Open();
        Exec("PRAGMA foreign_keys = OFF");
        Insert("Organisation", ("OrgID", 0L)); // every row defaults to OwnerID 0.
        if (withBaselineReleases)
        {
            Release(1, "4.2");
            Release(2, "4.3");
            Release(3, "4.4");
        }
    }

    // The empty native schema is built once per process (NativeSchema.CreateEmpty) and copied per test.
    private static readonly Lazy<string> Template = new(() =>
    {
        var directory = Path.Join(Path.GetTempPath(), $"native-plc-template_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "empty.db");
        NativeSchema.CreateEmpty(path, overwrite: false);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Left for the OS temp cleanup.
            }
        };
        return path;
    });

    private static string TemplatePath => Template.Value;

    public string FilePath { get; }

    public SqliteConnection Connection { get; }

    public long NextId() => ++_next;

    public void Dispose()
    {
        Connection.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Left for the OS temp cleanup.
        }
    }

    public void Exec(string sql)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public long Scalar(string sql)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- generic insert

    public void Insert(string table, params (string Column, object? Value)[] values)
    {
        if (!_columns.TryGetValue(table, out var columns))
        {
            columns = [];
            using var command = Connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table}\")";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns.Add((reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0));
            }

            _columns[table] = columns;
        }

        Assert.NotEmpty(columns); // the table exists in the native schema.
        var given = values.ToDictionary(v => v.Column, v => v.Value, StringComparer.Ordinal);
        foreach (var name in given.Keys)
        {
            Assert.Contains(columns, c => c.Name == name); // a misspelt column must not pass silently.
        }

        var names = new List<string>();
        var parameters = new List<(string Name, object Value)>();
        foreach (var (name, type, notNull) in columns)
        {
            if (given.TryGetValue(name, out var value))
            {
                names.Add(name);
                parameters.Add(($"$p{parameters.Count}", value ?? DBNull.Value));
            }
            else if (notNull)
            {
                names.Add(name);
                parameters.Add(($"$p{parameters.Count}", type.Equals("TEXT", StringComparison.OrdinalIgnoreCase) ? string.Empty : 0L));
            }
        }

        using var insert = Connection.CreateCommand();
        insert.CommandText =
            $"INSERT INTO \"{table}\" ({string.Join(", ", names.Select(n => "\"" + n + "\""))}) " +
            $"VALUES ({string.Join(", ", parameters.Select(p => p.Name))})";
        foreach (var (name, value) in parameters)
        {
            insert.Parameters.AddWithValue(name, value);
        }

        insert.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- dictionary rows

    public void Release(long releaseId, string code) =>
        Insert("Release", ("ReleaseID", releaseId), ("Code", code));

    public long Category(string code)
    {
        if (_categoryByCode.TryGetValue(code, out var id))
        {
            return id;
        }

        id = NextId();
        Insert("Category", ("CategoryID", id), ("Code", code), ("Name", code));
        _categoryByCode[code] = id;
        return id;
    }

    /// <summary>An item (member or property) of a category, valid from <paramref name="start"/> up to
    /// (not including) <paramref name="end"/>. Pass <paramref name="itemId"/> to add a second code of an
    /// existing item (its code changed between releases).</summary>
    public long Item(
        string categoryCode, string code, long start = 1, long? end = null, bool isDefault = false, long? itemId = null)
    {
        var id = itemId ?? NextId();
        if (itemId is null)
        {
            Insert("Item", ("ItemID", id), ("Name", code));
            if (categoryCode == "_PR")
            {
                Insert("Property", ("PropertyID", id)); // a property is an item with a row in Property.
            }
        }

        Insert(
            "ItemCategory",
            ("ItemID", id), ("StartReleaseID", start), ("EndReleaseID", end), ("CategoryID", Category(categoryCode)),
            ("Code", code), ("IsDefaultItem", isDefault ? 1L : 0L));
        return id;
    }

    /// <summary>A property: an item of the <c>_PR</c> category.</summary>
    public long Property(string code, long start = 1, long? end = null, bool isDefault = false) =>
        Item("_PR", code, start, end, isDefault);

    public long Member(string categoryCode, string code, long start = 1, long? end = null, bool isDefault = false) =>
        Item(categoryCode, code, start, end, isDefault);

    // ---------------------------------------------------------------- tables, headers, cells

    /// <summary>A table with one table version, valid from <paramref name="start"/> up to (not including)
    /// <paramref name="end"/>. Returns the <c>TableVID</c>. Call again with the same code to get a second
    /// table version of that code.</summary>
    public long TableVersion(string code, long start = 1, long? end = null)
    {
        var tableId = NextId();
        var tableVid = NextId();
        Insert("Table", ("TableID", tableId));
        Insert(
            "TableVersion",
            ("TableVID", tableVid), ("TableID", tableId), ("Code", code), ("Name", code),
            ("StartReleaseID", start), ("EndReleaseID", end));
        return tableVid;
    }

    /// <summary>A variable version of a variable of <paramref name="type"/> (<c>fact</c>, <c>key</c>),
    /// with its context: one <c>ContextComposition</c> pair per entry. Returns the <c>VariableVID</c>.</summary>
    public long Variable(string type, long? metricProperty, params (long Property, long Item)[] context)
    {
        var variableId = NextId();
        var variableVid = NextId();
        long? contextId = null;
        if (context.Length > 0)
        {
            contextId = NextId();
            Insert("Context", ("ContextID", contextId));
            foreach (var (property, item) in context)
            {
                Insert("ContextComposition", ("ContextID", contextId), ("PropertyID", property), ("ItemID", item));
            }
        }

        Insert("Variable", ("VariableID", variableId), ("Type", type));
        Insert(
            "VariableVersion",
            ("VariableVID", variableVid), ("VariableID", variableId), ("PropertyID", metricProperty ?? 0L), ("ContextID", contextId),
            ("StartReleaseID", 1L));
        return variableVid;
    }

    /// <summary>A header of a table version with its header version (code, property, sub-category
    /// version). Returns the <c>HeaderID</c>.</summary>
    public long Header(
        long tableVid, string direction, bool isKey, string code, long? propertyId = null, long? subCategoryVid = null)
    {
        var tableId = Scalar($"SELECT \"TableID\" FROM \"TableVersion\" WHERE \"TableVID\" = {tableVid}");
        var headerId = NextId();
        var headerVid = NextId();
        Insert("Header", ("HeaderID", headerId), ("TableID", tableId), ("Direction", direction), ("IsKey", isKey ? 1L : 0L));
        Insert(
            "HeaderVersion",
            ("HeaderVID", headerVid), ("HeaderID", headerId), ("Code", code), ("PropertyID", propertyId),
            ("SubCategoryVID", subCategoryVid), ("StartReleaseID", 1L));
        Insert("TableVersionHeader", ("TableVID", tableVid), ("HeaderID", headerId), ("HeaderVID", headerVid));
        return headerId;
    }

    /// <summary>A cell of a table version (<c>Cell</c> and <c>TableVersionCell</c>). Returns the <c>CellID</c>.</summary>
    public long Cell(
        long tableVid, long? variableVid, bool excluded = false, long? rowHeader = null, long? columnHeader = null,
        long? sheetHeader = null)
    {
        var tableId = Scalar($"SELECT \"TableID\" FROM \"TableVersion\" WHERE \"TableVID\" = {tableVid}");
        var cellId = NextId();
        Insert("Cell", ("CellID", cellId), ("TableID", tableId), ("RowID", rowHeader), ("ColumnID", columnHeader), ("SheetID", sheetHeader));
        Insert(
            "TableVersionCell",
            ("TableVID", tableVid), ("CellID", cellId), ("IsExcluded", excluded ? 1L : 0L), ("VariableVID", variableVid));
        return cellId;
    }

    /// <summary>A hierarchy (sub-category) of a domain with its version. Returns the <c>SubCategoryVID</c>.</summary>
    public long SubCategory(string domainCode, string hierarchyCode)
    {
        var subCategoryId = NextId();
        var subCategoryVid = NextId();
        Insert("SubCategory", ("SubCategoryID", subCategoryId), ("CategoryID", Category(domainCode)), ("Code", hierarchyCode));
        Insert("SubCategoryVersion", ("SubCategoryVID", subCategoryVid), ("SubCategoryID", subCategoryId), ("StartReleaseID", 1L));
        return subCategoryVid;
    }
}
