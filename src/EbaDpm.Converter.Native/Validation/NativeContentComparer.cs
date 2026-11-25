using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.Text;
using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Native.Catalog;
using EbaDpm.Converter.Native.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Validation;

/// <summary>What comparing one table found: rows read from the source, rows of the output that the
/// source does not have, and rows that are missing, extra or different (<see cref="Failed"/>).</summary>
internal sealed class TableComparison
{
    public long SourceRows { get; set; }

    public long ExtraRows { get; set; }

    public long Failed { get; set; }
}

/// <summary>
/// Compares the content of one exported table with the source, row by row (A-NAT-03). The source
/// values go through <see cref="NativeTypeMapping"/>, the same conversion as the copy, so what is
/// compared is exactly what the copy should have written.
///
/// A table whose primary key is made of integer columns is compared by a merge: both sides are
/// streamed ordered by the key and the orders agree (numeric). A table whose key is a Replication ID
/// (Access and SQLite do not order GUIDs alike) or that has no primary key is compared through a
/// dictionary of the source rows by key (the whole row when there is no key). The source is read by
/// one reader at a time: ACE OLEDB is not safe for concurrent use.
/// </summary>
internal static class NativeContentComparer
{
    private const char Separator = '\u001f';

    public static TableComparison Compare(
        OleDbConnection access, SqliteConnection output, NativeTable table, List<CheckSample> samples)
    {
        var forms = table.Columns.Select(c => NativeTypeMapping.Resolve(table.Name, c.Name, c.DataType).Form).ToArray();
        var keyIndexes = table.PrimaryKey
            .Select(k => IndexOfColumn(table, k))
            .ToArray();
        var mergeable = keyIndexes.Length > 0 && keyIndexes.All(i => i >= 0 && forms[i] == NativeValueForm.Integer);

        var comparison = new TableComparison();
        using var sourceCommand = SourceCommand(access, table, mergeable);
        using var source = sourceCommand.ExecuteReader(CommandBehavior.SequentialAccess);
        using var generatedCommand = OpenOutputCommand(output, table, mergeable);
        using var generated = generatedCommand.ExecuteReader();

        if (mergeable)
        {
            Merge(table, forms, keyIndexes, source, generated, comparison, samples);
        }
        else
        {
            HashJoin(table, forms, keyIndexes, source, generated, comparison, samples);
        }

        return comparison;
    }

    private static int IndexOfColumn(NativeTable table, string name)
    {
        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static OleDbCommand SourceCommand(OleDbConnection access, NativeTable table, bool orderByKey)
    {
        var select = "SELECT " + string.Join(", ", table.Columns.Select(c => NativeCheckSupport.AccessName(c.Name)))
            + " FROM " + NativeCheckSupport.AccessName(table.Name)
            + (orderByKey ? " ORDER BY " + string.Join(", ", table.PrimaryKey.Select(NativeCheckSupport.AccessName)) : string.Empty);
        return new OleDbCommand(select, access) { CommandTimeout = 0 };
    }

    private static SqliteCommand OpenOutputCommand(SqliteConnection output, NativeTable table, bool orderByKey)
    {
        var command = output.CreateCommand();
        command.CommandText = "SELECT " + string.Join(", ", table.Columns.Select(c => NativeSchemaBuilder.Quote(c.Name)))
            + " FROM " + NativeSchemaBuilder.Quote(table.Name)
            + (orderByKey ? " ORDER BY " + string.Join(", ", table.PrimaryKey.Select(NativeSchemaBuilder.Quote)) : string.Empty);
        return command;
    }

    private static bool ReadSource(OleDbDataReader reader, NativeValueForm[] forms, object?[] row)
    {
        if (!reader.Read())
        {
            return false;
        }

        for (var i = 0; i < forms.Length; i++)
        {
            row[i] = NativeTypeMapping.ToSqliteValue(forms[i], reader.GetValue(i));
        }

        return true;
    }

    private static bool ReadOutput(SqliteDataReader reader, object?[] row)
    {
        if (!reader.Read())
        {
            return false;
        }

        for (var i = 0; i < row.Length; i++)
        {
            row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }

        return true;
    }

    private static void Merge(
        NativeTable table, NativeValueForm[] forms, int[] keyIndexes, OleDbDataReader source, SqliteDataReader generated,
        TableComparison comparison, List<CheckSample> samples)
    {
        var sourceRow = new object?[forms.Length];
        var generatedRow = new object?[forms.Length];

        bool NextSource()
        {
            var has = ReadSource(source, forms, sourceRow);
            if (has)
            {
                comparison.SourceRows++;
            }

            return has;
        }

        var hasSource = NextSource();
        var hasGenerated = ReadOutput(generated, generatedRow);

        while (hasSource || hasGenerated)
        {
            var order = !hasSource ? 1 : !hasGenerated ? -1 : CompareKeys(keyIndexes, sourceRow, generatedRow);
            if (order < 0)
            {
                comparison.Failed++;
                AddSample(samples, KeyOf(table, keyIndexes, sourceRow), "<row missing>", Describe(table, sourceRow));
                hasSource = NextSource();
            }
            else if (order > 0)
            {
                comparison.Failed++;
                comparison.ExtraRows++;
                AddSample(samples, KeyOf(table, keyIndexes, generatedRow), Describe(table, generatedRow), "<row not in the source>");
                hasGenerated = ReadOutput(generated, generatedRow);
            }
            else
            {
                var differing = DifferingColumns(sourceRow, generatedRow);
                if (differing.Count > 0)
                {
                    comparison.Failed++;
                    AddSample(
                        samples, KeyOf(table, keyIndexes, sourceRow),
                        DescribeColumns(table, generatedRow, differing), DescribeColumns(table, sourceRow, differing));
                }

                hasSource = NextSource();
                hasGenerated = ReadOutput(generated, generatedRow);
            }
        }
    }

    private sealed class Entry(string text)
    {
        public string Text { get; } = text;

        public int Remaining { get; set; } = 1;
    }

    private static void HashJoin(
        NativeTable table, NativeValueForm[] forms, int[] keyIndexes, OleDbDataReader source, SqliteDataReader generated,
        TableComparison comparison, List<CheckSample> samples)
    {
        var hasKey = keyIndexes.Length > 0 && keyIndexes.All(i => i >= 0);
        var all = Enumerable.Range(0, forms.Length).ToArray();
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var row = new object?[forms.Length];

        while (ReadSource(source, forms, row))
        {
            comparison.SourceRows++;
            var text = RowText(row, all);
            var key = hasKey ? RowText(row, keyIndexes) : text;
            if (entries.TryGetValue(key, out var existing))
            {
                // A repeated key (or an identical row of a table without key): matched one by one.
                existing.Remaining++;
            }
            else
            {
                entries[key] = new Entry(text);
            }
        }

        while (ReadOutput(generated, row))
        {
            var text = RowText(row, all);
            var key = hasKey ? RowText(row, keyIndexes) : text;
            if (!entries.TryGetValue(key, out var entry) || entry.Remaining == 0)
            {
                comparison.Failed++;
                comparison.ExtraRows++;
                AddSample(samples, KeyOfRow(table, keyIndexes, hasKey, row), Describe(table, row), "<row not in the source>");
            }
            else
            {
                entry.Remaining--;
                if (!string.Equals(entry.Text, text, StringComparison.Ordinal))
                {
                    comparison.Failed++;
                    AddSample(samples, KeyOfRow(table, keyIndexes, hasKey, row), Display(text), Display(entry.Text));
                }
            }
        }

        foreach (var (key, entry) in entries)
        {
            if (entry.Remaining > 0)
            {
                comparison.Failed += entry.Remaining;
                AddSample(samples, $"{table.Name}|{(hasKey ? "pk=" : "row=")}{Display(key)}", "<row missing>", Display(entry.Text));
            }
        }
    }

    private static string Display(string text) => NativeCheckSupport.Shorten(text.Replace(Separator, '|'));

    private static string RowText(object?[] row, int[] indexes)
    {
        var builder = new StringBuilder();
        for (var n = 0; n < indexes.Length; n++)
        {
            if (n > 0)
            {
                builder.Append(Separator);
            }

            builder.Append(row[indexes[n]] switch
            {
                null => "\0NULL",
                long number => "i" + number.ToString(CultureInfo.InvariantCulture),
                string text => "s" + text,
                var other => "?" + other.GetType().Name + ":" + Convert.ToString(other, CultureInfo.InvariantCulture),
            });
        }

        return builder.ToString();
    }

    private static int CompareKeys(int[] keyIndexes, object?[] left, object?[] right)
    {
        foreach (var i in keyIndexes)
        {
            // Integer keys: both sides give long (a key of any other type would be a defect: it sorts first).
            var a = left[i] is long x ? x : long.MinValue;
            var b = right[i] is long y ? y : long.MinValue;
            var order = a.CompareTo(b);
            if (order != 0)
            {
                return order;
            }
        }

        return 0;
    }

    private static List<int> DifferingColumns(object?[] source, object?[] generated)
    {
        var differing = new List<int>();
        for (var i = 0; i < source.Length; i++)
        {
            if (!Equals(source[i], generated[i]))
            {
                differing.Add(i);
            }
        }

        return differing;
    }

    private static string Format(object? value) => value switch
    {
        null => "NULL",
        string text => "'" + NativeCheckSupport.Shorten(text, 60) + "'",
        long number => number.ToString(CultureInfo.InvariantCulture),
        var other => Convert.ToString(other, CultureInfo.InvariantCulture) + $" ({other.GetType().Name})",
    };

    private static string DescribeColumns(NativeTable table, object?[] row, List<int> columns)
        => string.Join("; ", columns.Take(4).Select(i => $"{table.Columns[i].Name}={Format(row[i])}"))
            + (columns.Count > 4 ? $"; ... {columns.Count - 4} more column(s)" : string.Empty);

    private static string Describe(NativeTable table, object?[] row)
        => NativeCheckSupport.Shorten(string.Join("; ", table.Columns.Select((c, i) => $"{c.Name}={Format(row[i])}")), 200);

    private static string KeyOf(NativeTable table, int[] keyIndexes, object?[] row)
        => $"{table.Name}|pk=" + string.Join(",", keyIndexes.Select((k, n) => $"{table.PrimaryKey[n]}={Format(row[k])}"));

    private static string KeyOfRow(NativeTable table, int[] keyIndexes, bool hasKey, object?[] row)
        => hasKey
            ? KeyOf(table, keyIndexes, row)
            : $"{table.Name}|row=" + Display(RowText(row, Enumerable.Range(0, row.Length).ToArray()));

    private static void AddSample(List<CheckSample> samples, string key, string generated, string reference)
    {
        if (samples.Count < CheckResult.MaxSamples)
        {
            samples.Add(new CheckSample(key, generated, reference));
        }
    }
}
