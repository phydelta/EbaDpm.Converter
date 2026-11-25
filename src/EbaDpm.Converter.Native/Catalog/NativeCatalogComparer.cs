namespace EbaDpm.Converter.Native.Catalog;

public enum NativeCatalogDifferenceKind
{
    TableAdded,
    TableRemoved,
    ColumnAdded,
    ColumnRemoved,
    ColumnTypeChanged,
    ColumnNullabilityChanged,
    ColumnOrderChanged,
    PrimaryKeyChanged,
    ForeignKeyAdded,
    ForeignKeyRemoved,
    ForeignKeyChanged,
    IndexAdded,
    IndexRemoved,
    IndexChanged,
}

/// <summary>
/// One difference between two catalogues. <see cref="Subject"/> is the business name of the object
/// (<c>Table</c>, <c>Table.Column</c>, <c>Table.ForeignKeyName</c>, <c>Table.IndexName</c>);
/// <see cref="Baseline"/> and <see cref="Actual"/> are its description on each side (null when it
/// does not exist there).
/// </summary>
public sealed record NativeCatalogDifference(
    NativeCatalogDifferenceKind Kind, string Subject, string? Baseline, string? Actual);

/// <summary>
/// Compares two catalogues by name (never by position) and lists the differences in a
/// deterministic order: tables (ordinal), then within each table its columns, primary key,
/// foreign keys and indexes. Equal catalogues give an empty list. It is the basis of the schema
/// drift report: it does not decide anything about the consequences of a difference.
/// </summary>
public static class NativeCatalogComparer
{
    public static IReadOnlyList<NativeCatalogDifference> Compare(NativeCatalog baseline, NativeCatalog actual)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(actual);

        var differences = new List<NativeCatalogDifference>();
        var baselineTables = baseline.Tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var actualTables = actual.Tables.ToDictionary(t => t.Name, StringComparer.Ordinal);

        foreach (var name in baselineTables.Keys.Union(actualTables.Keys).Order(StringComparer.Ordinal))
        {
            var inBaseline = baselineTables.TryGetValue(name, out var before);
            var inActual = actualTables.TryGetValue(name, out var after);
            if (!inActual)
            {
                differences.Add(new(NativeCatalogDifferenceKind.TableRemoved, name, Describe(before!), null));
            }
            else if (!inBaseline)
            {
                differences.Add(new(NativeCatalogDifferenceKind.TableAdded, name, null, Describe(after!)));
            }
            else
            {
                CompareTable(before!, after!, differences);
            }
        }

        CompareKeyed(
            baseline.ForeignKeys, actual.ForeignKeys, fk => fk.ChildTable + "." + fk.Name, Describe,
            NativeCatalogDifferenceKind.ForeignKeyAdded, NativeCatalogDifferenceKind.ForeignKeyRemoved,
            NativeCatalogDifferenceKind.ForeignKeyChanged, differences);
        CompareKeyed(
            baseline.Indexes, actual.Indexes, index => index.Table + "." + index.Name, Describe,
            NativeCatalogDifferenceKind.IndexAdded, NativeCatalogDifferenceKind.IndexRemoved,
            NativeCatalogDifferenceKind.IndexChanged, differences);

        return differences;
    }

    private static void CompareTable(NativeTable before, NativeTable after, List<NativeCatalogDifference> differences)
    {
        var beforeColumns = before.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var afterColumns = after.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);

        foreach (var name in beforeColumns.Keys.Union(afterColumns.Keys).Order(StringComparer.Ordinal))
        {
            var subject = before.Name + "." + name;
            var inBefore = beforeColumns.TryGetValue(name, out var oldColumn);
            var inAfter = afterColumns.TryGetValue(name, out var newColumn);
            if (!inAfter)
            {
                differences.Add(new(NativeCatalogDifferenceKind.ColumnRemoved, subject, Describe(oldColumn!), null));
            }
            else if (!inBefore)
            {
                differences.Add(new(NativeCatalogDifferenceKind.ColumnAdded, subject, null, Describe(newColumn!)));
            }
            else
            {
                if (oldColumn!.DataType != newColumn!.DataType)
                {
                    differences.Add(new(
                        NativeCatalogDifferenceKind.ColumnTypeChanged, subject,
                        oldColumn.DataType.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        newColumn.DataType.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }

                if (oldColumn.Nullable != newColumn.Nullable)
                {
                    differences.Add(new(
                        NativeCatalogDifferenceKind.ColumnNullabilityChanged, subject,
                        oldColumn.Nullable ? "NULL" : "NOT NULL", newColumn.Nullable ? "NULL" : "NOT NULL"));
                }
            }
        }

        // Order of the common columns (a pure addition or removal does not count as a reorder).
        var commonBefore = before.Columns.Where(c => afterColumns.ContainsKey(c.Name)).Select(c => c.Name);
        var commonAfter = after.Columns.Where(c => beforeColumns.ContainsKey(c.Name)).Select(c => c.Name);
        if (!commonBefore.SequenceEqual(commonAfter, StringComparer.Ordinal))
        {
            differences.Add(new(
                NativeCatalogDifferenceKind.ColumnOrderChanged, before.Name,
                string.Join(", ", commonBefore), string.Join(", ", commonAfter)));
        }

        if (!before.PrimaryKey.SequenceEqual(after.PrimaryKey, StringComparer.Ordinal))
        {
            differences.Add(new(
                NativeCatalogDifferenceKind.PrimaryKeyChanged, before.Name,
                string.Join(", ", before.PrimaryKey), string.Join(", ", after.PrimaryKey)));
        }
    }

    private static void CompareKeyed<T>(
        IReadOnlyList<T> baseline,
        IReadOnlyList<T> actual,
        Func<T, string> key,
        Func<T, string> describe,
        NativeCatalogDifferenceKind added,
        NativeCatalogDifferenceKind removed,
        NativeCatalogDifferenceKind changed,
        List<NativeCatalogDifference> differences)
        where T : class
    {
        var before = baseline.ToDictionary(key, StringComparer.Ordinal);
        var after = actual.ToDictionary(key, StringComparer.Ordinal);
        foreach (var name in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var inBefore = before.TryGetValue(name, out var oldItem);
            var inAfter = after.TryGetValue(name, out var newItem);
            if (!inAfter)
            {
                differences.Add(new(removed, name, describe(oldItem!), null));
            }
            else if (!inBefore)
            {
                differences.Add(new(added, name, null, describe(newItem!)));
            }
            else if (!oldItem!.Equals(newItem))
            {
                differences.Add(new(changed, name, describe(oldItem), describe(newItem!)));
            }
        }
    }

    private static string Describe(NativeTable table)
        => $"{table.Columns.Count} columns, primary key ({string.Join(", ", table.PrimaryKey)})";

    private static string Describe(NativeColumn column)
        => $"type {column.DataType}, {(column.Nullable ? "NULL" : "NOT NULL")}";

    private static string Describe(NativeForeignKey fk)
        => $"({string.Join(", ", fk.ChildColumns)}) -> {fk.ParentTable} ({string.Join(", ", fk.ParentColumns)}) "
            + $"ON UPDATE {fk.UpdateRule} ON DELETE {fk.DeleteRule}";

    private static string Describe(NativeIndex index)
        => $"{(index.Unique ? "unique " : string.Empty)}({string.Join(", ", index.Columns)})";
}
