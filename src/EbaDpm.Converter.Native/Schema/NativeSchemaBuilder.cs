using System.Text;
using EbaDpm.Converter.Native.Catalog;

namespace EbaDpm.Converter.Native.Schema;

/// <summary>
/// The SQLite DDL of the native output for one catalogue, in the order it must be run: the
/// <see cref="TableStatements"/> (before the load) and the <see cref="IndexStatements"/> (after it).
/// </summary>
public sealed record NativeSchemaDdl(
    IReadOnlyList<NativeTable> Tables,
    IReadOnlyList<string> TableStatements,
    IReadOnlyList<string> IndexStatements,
    IReadOnlyList<NativeForeignKey> ForeignKeysDeclared,
    IReadOnlyList<NativeForeignKey> ForeignKeysDropped,
    IReadOnlyList<string> AccessIndexNames,
    IReadOnlyList<string> ForeignKeyIndexNames)
{
    /// <summary>Every statement, tables first.</summary>
    public IEnumerable<string> Statements => TableStatements.Concat(IndexStatements);
}

/// <summary>
/// Builds the DDL of the native output from a catalogue (docs/target-schema-native.md, "Keys,
/// relationships and indexes"). Deterministic: the same catalogue gives byte-identical text.
///
/// Tables: every table that is not in <see cref="NativeExclusions"/>, in ordinal order of the name,
/// with the columns in Access order, the types of <see cref="NativeTypeMapping"/>, <c>NOT NULL</c>
/// where Access requires the column, the primary key and the foreign keys with their Access rules
/// (a relationship that touches an excluded table is not declared). Indexes: each non-primary-key
/// Access index named <c>"&lt;Table&gt;_&lt;Access index name&gt;"</c> (identical column lists are
/// deduplicated, and one identical to the primary key is skipped), then, for each foreign key whose
/// child columns no index (the primary key included) starts with, an index
/// <c>"&lt;Table&gt;_FK_&lt;Column&gt;"</c>. Identifiers are always quoted.
/// </summary>
public static class NativeSchemaBuilder
{
    private static readonly HashSet<string> Rules = new(StringComparer.Ordinal) { "CASCADE", "NO ACTION", "SET NULL", "SET DEFAULT" };

    public static NativeSchemaDdl Build(NativeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var tables = catalog.Tables
            .Where(t => !NativeExclusions.IsExcluded(t.Name))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
        var exported = tables.ToDictionary(t => t.Name, StringComparer.Ordinal);

        var declared = new List<NativeForeignKey>();
        var dropped = new List<NativeForeignKey>();
        foreach (var fk in catalog.ForeignKeys.OrderBy(f => f.ChildTable, StringComparer.Ordinal).ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            (exported.ContainsKey(fk.ChildTable) && exported.ContainsKey(fk.ParentTable) ? declared : dropped).Add(fk);
        }

        var tableStatements = tables
            .Select(table => CreateTable(table, declared.Where(fk => fk.ChildTable == table.Name).ToList()))
            .ToList();

        var indexStatements = new List<string>();
        var accessIndexNames = new List<string>();
        var foreignKeyIndexNames = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in tables)
        {
            var emitted = new List<IReadOnlyList<string>> { table.PrimaryKey };

            // Access indexes, deduplicated by column list; the unique one wins among identical lists.
            var candidates = catalog.Indexes
                .Where(index => string.Equals(index.Table, table.Name, StringComparison.Ordinal))
                .OrderBy(index => index.Name, StringComparer.Ordinal)
                .GroupBy(index => string.Join('\u001f', index.Columns), StringComparer.Ordinal)
                .Select(group => group.FirstOrDefault(index => index.Unique) ?? group.First())
                .OrderBy(index => index.Name, StringComparer.Ordinal);

            foreach (var index in candidates)
            {
                if (index.Columns.SequenceEqual(table.PrimaryKey, StringComparer.Ordinal))
                {
                    continue;
                }

                var name = table.Name + "_" + index.Name;
                indexStatements.Add(CreateIndex(name, table.Name, index.Columns, index.Unique, usedNames));
                accessIndexNames.Add(name);
                emitted.Add(index.Columns);
            }

            // Child columns of every foreign key that no index starts with.
            foreach (var fk in declared.Where(f => f.ChildTable == table.Name))
            {
                if (emitted.Any(columns => columns.Take(fk.ChildColumns.Count).SequenceEqual(fk.ChildColumns, StringComparer.Ordinal)))
                {
                    continue;
                }

                var name = table.Name + "_FK_" + string.Join('_', fk.ChildColumns);
                indexStatements.Add(CreateIndex(name, table.Name, fk.ChildColumns, unique: false, usedNames));
                foreignKeyIndexNames.Add(name);
                emitted.Add(fk.ChildColumns);
            }
        }

        return new NativeSchemaDdl(tables, tableStatements, indexStatements, declared, dropped, accessIndexNames, foreignKeyIndexNames);
    }

    /// <summary>An identifier in double quotes (embedded quotes doubled).</summary>
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string QuoteList(IEnumerable<string> identifiers) => string.Join(", ", identifiers.Select(Quote));

    private static string CreateTable(NativeTable table, IReadOnlyList<NativeForeignKey> foreignKeys)
    {
        if (table.Columns.Count == 0)
        {
            throw new InvalidOperationException($"Table '{table.Name}' has no columns in the catalogue.");
        }

        var lines = new List<string>();
        foreach (var column in table.Columns)
        {
            var mapping = NativeTypeMapping.Resolve(table.Name, column.Name, column.DataType);
            lines.Add($"{Quote(column.Name)} {mapping.SqliteType}{(column.Nullable ? string.Empty : " NOT NULL")}");
        }

        if (table.PrimaryKey.Count > 0)
        {
            lines.Add($"PRIMARY KEY ({QuoteList(table.PrimaryKey)})");
        }

        foreach (var fk in foreignKeys)
        {
            if (!Rules.Contains(fk.UpdateRule) || !Rules.Contains(fk.DeleteRule))
            {
                throw new InvalidOperationException(
                    $"Relationship '{fk.Name}' of '{fk.ChildTable}' has an unknown rule (update '{fk.UpdateRule}', delete '{fk.DeleteRule}').");
            }

            lines.Add(
                $"FOREIGN KEY ({QuoteList(fk.ChildColumns)}) REFERENCES {Quote(fk.ParentTable)} ({QuoteList(fk.ParentColumns)}) "
                + $"ON UPDATE {fk.UpdateRule} ON DELETE {fk.DeleteRule}");
        }

        var builder = new StringBuilder();
        builder.Append("CREATE TABLE ").Append(Quote(table.Name)).Append(" (\n");
        builder.Append(string.Join(",\n", lines.Select(line => "    " + line)));
        builder.Append("\n)");
        return builder.ToString();
    }

    private static string CreateIndex(string name, string table, IReadOnlyList<string> columns, bool unique, HashSet<string> usedNames)
    {
        if (!usedNames.Add(name))
        {
            throw new InvalidOperationException($"Two indexes of the native schema would be named '{name}'.");
        }

        return $"CREATE {(unique ? "UNIQUE " : string.Empty)}INDEX {Quote(name)} ON {Quote(table)} ({QuoteList(columns)})";
    }
}
