using EbaDpm.Converter.Core.Layouts;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.PlaneC;

/// <summary>Small read helpers over a SQLite connection (read-only use).</summary>
internal static class NativeSql
{
    public static void Query(SqliteConnection connection, string sql, Action<SqliteDataReader> onRow)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            onRow(reader);
        }
    }

    public static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """SELECT COUNT(1) FROM "sqlite_master" WHERE "type" = 'table' AND "name" = $name""";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    public static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""PRAGMA table_info("{table}")""";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static long? NullableLong(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}

/// <summary>The releases of a native file: <c>Release.Code</c> -> <c>ReleaseID</c>. A code that two
/// rows share is ambiguous and never resolves (nothing is guessed).</summary>
internal sealed class NativeReleases
{
    private readonly Dictionary<string, long> _idByCode = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ambiguous = new(StringComparer.Ordinal);

    public static NativeReleases Load(SqliteConnection native)
    {
        var releases = new NativeReleases();
        NativeSql.Query(native, """SELECT "ReleaseID", "Code" FROM "Release" WHERE "Code" IS NOT NULL""", r =>
        {
            var code = r.GetString(1);
            if (!releases._idByCode.TryAdd(code, r.GetInt64(0)))
            {
                releases._ambiguous.Add(code);
            }
        });
        return releases;
    }

    public bool TryGetId(string code, out long releaseId)
    {
        releaseId = 0;
        return !_ambiguous.Contains(code) && _idByCode.TryGetValue(code, out releaseId);
    }
}

internal enum TableVersionLookup
{
    Found,
    NotValidAtRelease,
    Several,
}

/// <summary>The table versions of a native file, found by code and release with the validity
/// predicate <c>StartReleaseID &lt;= R AND (EndReleaseID IS NULL OR EndReleaseID &gt; R)</c>.</summary>
internal sealed class NativeTableVersions
{
    private sealed record Row(long TableVid, long Start, long? End);

    private readonly Dictionary<string, List<Row>> _byCode = new(StringComparer.Ordinal);

    public static NativeTableVersions Load(SqliteConnection native)
    {
        var versions = new NativeTableVersions();
        NativeSql.Query(
            native,
            """SELECT "TableVID", "Code", "StartReleaseID", "EndReleaseID" FROM "TableVersion" WHERE "Code" IS NOT NULL""",
            r =>
            {
                // Same canonical form as the layout side (symmetric normalisation, a no-op for native codes).
                var code = LayoutTableCodeNormalizer.Canonicalize(r.GetString(1));
                if (!versions._byCode.TryGetValue(code, out var list))
                {
                    list = [];
                    versions._byCode[code] = list;
                }

                list.Add(new Row(r.GetInt64(0), r.GetInt64(2), NativeSql.NullableLong(r, 3)));
            });
        return versions;
    }

    public TableVersionLookup Find(string tableCode, long releaseId, out long tableVid)
    {
        tableVid = 0;
        if (!_byCode.TryGetValue(tableCode, out var rows))
        {
            return TableVersionLookup.NotValidAtRelease;
        }

        var valid = rows.Where(row => IsValid(row.Start, row.End, releaseId)).ToList();
        if (valid.Count == 0)
        {
            return TableVersionLookup.NotValidAtRelease;
        }

        if (valid.Count > 1)
        {
            return TableVersionLookup.Several;
        }

        tableVid = valid[0].TableVid;
        return TableVersionLookup.Found;
    }

    /// <summary>The <c>TableVID</c> of every table version valid at <paramref name="releaseId"/> whose
    /// code is unique among the versions valid at that release.</summary>
    public IEnumerable<long> UniqueValidAt(long releaseId)
    {
        foreach (var rows in _byCode.Values)
        {
            var valid = rows.Where(row => IsValid(row.Start, row.End, releaseId)).ToList();
            if (valid.Count == 1)
            {
                yield return valid[0].TableVid;
            }
        }
    }

    public static bool IsValid(long start, long? end, long releaseId) =>
        start <= releaseId && (end is null || end > releaseId);
}

/// <summary>A code of an item at a release, or why there is none.</summary>
internal enum ItemCodeStatus
{
    Found,
    Missing,
    Ambiguous,
}

/// <summary>The code of an item (member or property) at a release: <c>ItemCategory.Code</c> of the
/// row of that <c>ItemID</c> valid at the release; <c>IsDefaultItem</c> comes from the same row.</summary>
internal sealed class NativeItemCodes
{
    private sealed record Row(long Start, long? End, string? Code, bool IsDefault);

    private readonly Dictionary<long, List<Row>> _byItem = [];

    public static NativeItemCodes Load(SqliteConnection native)
    {
        var codes = new NativeItemCodes();
        NativeSql.Query(
            native,
            """SELECT "ItemID", "StartReleaseID", "EndReleaseID", "Code", "IsDefaultItem" FROM "ItemCategory" """,
            r =>
            {
                var itemId = r.GetInt64(0);
                if (!codes._byItem.TryGetValue(itemId, out var list))
                {
                    list = [];
                    codes._byItem[itemId] = list;
                }

                list.Add(new Row(
                    r.GetInt64(1), NativeSql.NullableLong(r, 2), NativeSql.NullableString(r, 3),
                    !r.IsDBNull(4) && r.GetInt64(4) != 0));
            });
        return codes;
    }

    public ItemCodeStatus TryGet(long itemId, long releaseId, out string code, out bool isDefault)
    {
        code = string.Empty;
        isDefault = false;
        if (!_byItem.TryGetValue(itemId, out var rows))
        {
            return ItemCodeStatus.Missing;
        }

        Row? found = null;
        foreach (var row in rows)
        {
            if (!NativeTableVersions.IsValid(row.Start, row.End, releaseId))
            {
                continue;
            }

            if (found is not null)
            {
                return ItemCodeStatus.Ambiguous;
            }

            found = row;
        }

        if (found?.Code is null)
        {
            return ItemCodeStatus.Missing;
        }

        code = found.Code;
        isDefault = found.IsDefault;
        return ItemCodeStatus.Found;
    }
}

internal sealed record NativeCell(long CellId, bool IsExcluded, long? VariableVid, long? ColumnId, long? RowId, long? SheetId);

internal sealed record NativeHeader(long HeaderId, string? Direction, bool IsKey, string? Code, long? PropertyId, long? SubCategoryVid);

/// <summary>The cells and headers of one table version.</summary>
internal sealed class NativeTableData(long tableVid)
{
    public long TableVid { get; } = tableVid;

    public List<NativeCell> Cells { get; } = [];

    public List<NativeHeader> Headers { get; } = [];
}

/// <summary>Cells, headers, variables and contexts of the table versions that plane C reads, loaded
/// once with a streaming pass per table (the native file has hundreds of thousands of cells and
/// 1.7 million context pairs: only what the selected table versions use is kept).</summary>
internal sealed class NativeStructure
{
    public Dictionary<long, NativeTableData> Tables { get; } = [];

    /// <summary><c>VariableVID</c> -&gt; (<c>PropertyID</c>, <c>ContextID</c>).</summary>
    public Dictionary<long, (long? PropertyId, long? ContextId)> Variables { get; } = [];

    /// <summary><c>ContextID</c> -&gt; (<c>PropertyID</c>, <c>ItemID</c>) pairs.</summary>
    public Dictionary<long, List<(long PropertyId, long ItemId)>> Contexts { get; } = [];

    public static NativeStructure Load(SqliteConnection native, IReadOnlySet<long> tableVids)
    {
        var structure = new NativeStructure();
        foreach (var vid in tableVids)
        {
            structure.Tables[vid] = new NativeTableData(vid);
        }

        var variableVids = new HashSet<long>();
        NativeSql.Query(
            native,
            """
            SELECT tvc."TableVID", tvc."CellID", tvc."IsExcluded", tvc."VariableVID", c."ColumnID", c."RowID", c."SheetID"
            FROM "TableVersionCell" tvc JOIN "Cell" c ON c."CellID" = tvc."CellID"
            """,
            r =>
            {
                if (!structure.Tables.TryGetValue(r.GetInt64(0), out var table))
                {
                    return;
                }

                var variableVid = NativeSql.NullableLong(r, 3);
                table.Cells.Add(new NativeCell(
                    r.GetInt64(1), !r.IsDBNull(2) && r.GetInt64(2) != 0, variableVid,
                    NativeSql.NullableLong(r, 4), NativeSql.NullableLong(r, 5), NativeSql.NullableLong(r, 6)));
                if (variableVid is { } v)
                {
                    variableVids.Add(v);
                }
            });

        NativeSql.Query(
            native,
            """
            SELECT tvh."TableVID", h."HeaderID", h."Direction", h."IsKey", hv."Code", hv."PropertyID", hv."SubCategoryVID"
            FROM "TableVersionHeader" tvh
            JOIN "Header" h ON h."HeaderID" = tvh."HeaderID"
            LEFT JOIN "HeaderVersion" hv ON hv."HeaderVID" = tvh."HeaderVID"
            """,
            r =>
            {
                if (!structure.Tables.TryGetValue(r.GetInt64(0), out var table))
                {
                    return;
                }

                table.Headers.Add(new NativeHeader(
                    r.GetInt64(1), NativeSql.NullableString(r, 2), !r.IsDBNull(3) && r.GetInt64(3) != 0,
                    NativeSql.NullableString(r, 4)?.Trim(), NativeSql.NullableLong(r, 5), NativeSql.NullableLong(r, 6)));
            });

        var contextIds = new HashSet<long>();
        NativeSql.Query(
            native,
            """SELECT "VariableVID", "PropertyID", "ContextID" FROM "VariableVersion" """,
            r =>
            {
                var variableVid = r.GetInt64(0);
                if (!variableVids.Contains(variableVid))
                {
                    return;
                }

                var contextId = NativeSql.NullableLong(r, 2);
                structure.Variables[variableVid] = (NativeSql.NullableLong(r, 1), contextId);
                if (contextId is { } c)
                {
                    contextIds.Add(c);
                }
            });

        NativeSql.Query(
            native,
            """SELECT "ContextID", "PropertyID", "ItemID" FROM "ContextComposition" """,
            r =>
            {
                var contextId = r.GetInt64(0);
                if (!contextIds.Contains(contextId))
                {
                    return;
                }

                if (!structure.Contexts.TryGetValue(contextId, out var list))
                {
                    list = [];
                    structure.Contexts[contextId] = list;
                }

                list.Add((r.GetInt64(1), r.GetInt64(2)));
            });

        return structure;
    }
}
