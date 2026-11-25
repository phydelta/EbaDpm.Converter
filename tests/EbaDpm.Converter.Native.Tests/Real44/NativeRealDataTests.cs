using System.Data.OleDb;
using System.Globalization;
using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Native.Catalog;
using EbaDpm.Converter.Native.Schema;
using EbaDpm.Converter.Native.Tests.Support;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.Real44;

/// <summary>
/// The native conversion of the real 4.4 database (<c>DPM2 Database_v 4_4_20261006.accdb</c>): the
/// catalogue equals the embedded baseline, and the converted file is the Access database table for
/// table. Every check that compares with Access has a positive control: the same comparison is
/// run against a deliberately tampered output (a transaction that is rolled back) and must report
/// the difference.
/// </summary>
[Collection(Native44Collection.Name)]
public sealed class NativeRealDataTests(Native44Fixture fixture) : IDisposable
{
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TempFiles.ReleaseAccess();
    }

    private static OleDbConnection OpenAccess(string path)
    {
        var connection = new OleDbConnection(SyntheticDpm2Accdb.ConnectionString(path));
        connection.Open();
        return connection;
    }

    private void RequireConverted()
        => Assert.True(fixture.Converted, "The 4.4 conversion did not run although the data directory exists (is ACE installed?).");

    // ------------------------------------------------------------------ catalogue and DDL

    [DataFact]
    public void Catalogue_ReadFromTheAccess44Database_EqualsTheEmbeddedBaseline_AndTheDdlIsByteIdentical()
    {
        RepoPaths.EnsureAccessDpm20Release44DatabaseExists();
        var baseline = NativeBaseline.Load();

        var actual = NativeCatalogReader.Read(fixture.SourcePath);
        OleDbConnection.ReleaseObjectPool();

        var differences = NativeCatalogComparer.Compare(baseline.Catalog, actual);
        Assert.True(
            differences.Count == 0,
            "Catalogue differences against the baseline:\n" + string.Join("\n", differences.Take(30).Select(d => $"{d.Kind} {d.Subject}: {d.Baseline} -> {d.Actual}")));
        Assert.Equal(baseline.Catalog, actual);

        var fromBaseline = string.Join("\n", NativeSchemaBuilder.Build(baseline.Catalog).Statements);
        var fromAccess = string.Join("\n", NativeSchemaBuilder.Build(actual).Statements);
        Assert.Equal(
            System.Text.Encoding.UTF8.GetBytes(fromBaseline),
            System.Text.Encoding.UTF8.GetBytes(fromAccess));
        Assert.Equal(70, actual.Tables.Count); // 59 exported + 11 excluded: the catalogue is complete
        Assert.Equal(59, NativeSchemaBuilder.Build(actual).Tables.Count);
    }

    [Fact]
    public void CatalogueComparison_PositiveControl_AnyModifiedCatalogueIsSeenAndGivesDifferentDdl()
    {
        var baseline = NativeBaseline.Load().Catalog;
        var ddl = string.Join("\n", NativeSchemaBuilder.Build(baseline).Statements);

        var exported = baseline.Tables.First(t => !NativeExclusions.IsExcluded(t.Name) && t.Columns.Count > 2);
        var changed = exported.Columns.First(c => !exported.PrimaryKey.Contains(c.Name));
        var modifications = new Dictionary<string, NativeCatalog>
        {
            ["retyped column"] = Replace(baseline, exported, exported with
            {
                Columns = [.. exported.Columns.Select(c => c == changed ? c with { DataType = c.DataType == 130 ? 3 : 130 } : c)],
            }),
            ["nullability"] = Replace(baseline, exported, exported with
            {
                Columns = [.. exported.Columns.Select(c => c == changed ? c with { Nullable = !c.Nullable } : c)],
            }),
            ["removed column"] = Replace(baseline, exported, exported with { Columns = [.. exported.Columns.Where(c => c != changed)] }),
            ["removed foreign key"] = baseline with { ForeignKeys = [.. baseline.ForeignKeys.Skip(1)] },
            ["removed index"] = baseline with { Indexes = [.. baseline.Indexes.Skip(1)] },
            ["added table"] = baseline with { Tables = [.. baseline.Tables, new NativeTable("NewEbaTable", [new("Id", 3, false)], ["Id"])] },
        };

        foreach (var (name, modified) in modifications)
        {
            Assert.NotEqual(baseline, modified);
            Assert.NotEmpty(NativeCatalogComparer.Compare(baseline, modified));
            Assert.True(
                ddl != string.Join("\n", NativeSchemaBuilder.Build(modified).Statements),
                $"The DDL did not change for the modification '{name}'.");
        }
    }

    private static NativeCatalog Replace(NativeCatalog catalog, NativeTable before, NativeTable after)
        => catalog with { Tables = [.. catalog.Tables.Select(t => t == before ? after : t)] };

    // ------------------------------------------------------------------ the conversion

    [DataFact]
    public void Conversion_ExitsZero_Writes59Tables_AndReportsTheBaselineSchema()
    {
        RequireConverted();

        Assert.Equal(CliRunner.ExitOk, fixture.ExitCode);
        Assert.Empty(fixture.Stderr);
        Assert.Contains("Tables: 59.", fixture.Stdout, StringComparison.Ordinal);
        Assert.Contains("identical to the 4.4 baseline", fixture.Stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(fixture.OutputPath));
        Assert.False(File.Exists(fixture.OutputPath + ".partial"));
        using var sqlite = AccessSqliteComparer.OpenSqlite(fixture.OutputPath);
        Assert.Equal(59, AccessSqliteComparer.SqliteUserTables(sqlite).Count);
    }

    [DataFact]
    public void TableList_IsTheSourceUserTablesMinusTheElevenNamed()
    {
        RequireConverted();
        using var access = OpenAccess(fixture.SourcePath);
        using var sqlite = AccessSqliteComparer.OpenSqlite(fixture.OutputPath);

        var source = AccessSqliteComparer.AccessUserTables(access);
        var expected = source.Except(NativeExclusions.ExcludedTables, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var actual = AccessSqliteComparer.SqliteUserTables(sqlite).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(70, source.Count);
        Assert.Equal(59, expected.Count);
        Assert.Equal(expected, actual);
        foreach (var excluded in NativeExclusions.ExcludedTables)
        {
            Assert.Contains(excluded, source); // the source has it ...
            Assert.DoesNotContain(excluded, actual); // ... the output does not
        }
    }

    [DataFact]
    public void EveryTable_RowCount_EqualsAccessCount()
    {
        RequireConverted();
        using var access = OpenAccess(fixture.SourcePath);
        using var sqlite = AccessSqliteComparer.OpenSqlite(fixture.OutputPath);
        var tables = NativeOutputChecks.Exported(AccessProbe.Read(access));

        var differences = NativeOutputChecks.RowCountDifferences(access, sqlite, tables);

        Assert.Equal(59, tables.Length);
        AccessSqliteComparer.AssertNone(differences);
    }

    [DataFact]
    public void RowCount_PositiveControl_ADeletedRowIsDetected()
    {
        RequireConverted();
        using var access = OpenAccess(fixture.SourcePath);
        using var sqlite = AccessSqliteComparer.OpenSqlite(fixture.OutputPath, readOnly: false);
        string[] tables = ["Release", "Table", "Reference"];

        using var transaction = sqlite.BeginTransaction();
        Execute(sqlite, "DELETE FROM \"Release\" WHERE ReleaseID = (SELECT MAX(ReleaseID) FROM \"Release\")");

        Assert.Contains(NativeOutputChecks.RowCountDifferences(access, sqlite, tables), d => d.StartsWith("Release:", StringComparison.Ordinal));
        transaction.Rollback();
        AccessSqliteComparer.AssertNone(NativeOutputChecks.RowCountDifferences(access, sqlite, tables));
    }

    [DataFact]
    public void Values_SampledFromEveryTable_AreIdenticalToAccess_ForEveryType()
    {
        RequireConverted();
        using var access = OpenAccess(fixture.SourcePath);
        using var sqlite = AccessSqliteComparer.OpenSqlite(fixture.OutputPath);
        var probe = AccessProbe.Read(access);

        var differences = new List<string>();
        var byKind = Enum.GetValues<AccessSqliteComparer.Kind>().ToDictionary(k => k, _ => 0L);
        long rows = 0;
        foreach (var table in NativeOutputChecks.Exported(probe))
        {
            var columns = probe.ColumnsOf(table).Select(c => c.Name).ToList();
            var key = probe.PrimaryKeys.GetValueOrDefault(table);
            Assert.NotNull(key); // every exported table of 4.4 has a primary key to look rows up by
            foreach (var descending in new[] { false, true })
            {
                var result = AccessSqliteComparer.Compare(access, sqlite, table, columns, key, top: 150, descending: descending);
                differences.AddRange(result.Differences);
                rows += result.RowsCompared;
                foreach (var (kind, count) in result.CellsByKind)
                {
                    byKind[kind] += count;
                }
            }
        }

        AccessSqliteComparer.AssertNone(differences);
        Assert.True(rows > 5000, $"Only {rows} rows were sampled.");
        foreach (var (kind, count) in byKind)
        {
            Assert.True(count > 0, $"No cell of kind {kind} was compared: the sample would not notice an error of that type.");
        }
    }

    [DataFact]
    public void Values_PositiveControl_TamperedGuidBooleanAndTextAreDetected()
    {
        RequireConverted();
        using var access = OpenAccess(fixture.SourcePath);
        using var sqlite = AccessSqliteComparer.OpenSqlite(fixture.OutputPath, readOnly: false);
        var columns = AccessSqliteComparer.SqliteColumns(sqlite, "Release").Select(c => c.Name).ToArray();
        Assert.Contains("RowGUID", columns);
        Assert.Contains("IsCurrent", columns);
        Assert.Contains("Code", columns);

        IReadOnlyList<string> Sample() => AccessSqliteComparer.Compare(access, sqlite, "Release", columns, ["ReleaseID"], top: 50).Differences;

        void Tampered(string sql, string expectedColumn)
        {
            using var transaction = sqlite.BeginTransaction();
            Execute(sqlite, sql);
            Assert.Contains(Sample(), d => d.Contains(expectedColumn, StringComparison.Ordinal));
            transaction.Rollback();
        }

        Assert.Empty(Sample());
        const string first = "WHERE ReleaseID = (SELECT MIN(ReleaseID) FROM \"Release\")";
        Tampered($"UPDATE \"Release\" SET RowGUID = upper(RowGUID) {first}", "RowGUID");
        Tampered($"UPDATE \"Release\" SET IsCurrent = 1 - IsCurrent {first}", "IsCurrent");
        Tampered($"UPDATE \"Release\" SET Code = Code || ' ' {first}", "Code");
        Assert.Empty(Sample());
    }

    [DataFact]
    public void EveryColumn_Fingerprints_CountsSumsAndTextLengths_EqualAccess()
    {
        RequireConverted();
        using var access = OpenAccess(fixture.SourcePath);
        using var sqlite = AccessSqliteComparer.OpenSqlite(fixture.OutputPath);
        var probe = AccessProbe.Read(access);

        var differences = NativeOutputChecks.FingerprintDifferences(access, sqlite, probe, NativeOutputChecks.Exported(probe));

        AccessSqliteComparer.AssertNone(differences);
    }

    [DataFact]
    public void Fingerprints_PositiveControl_TrimmedTextAndFlippedBooleanAreDetected()
    {
        RequireConverted();
        using var access = OpenAccess(fixture.SourcePath);
        using var sqlite = AccessSqliteComparer.OpenSqlite(fixture.OutputPath, readOnly: false);
        var probe = AccessProbe.Read(access);
        string[] tables = ["Release"];

        AccessSqliteComparer.AssertNone(NativeOutputChecks.FingerprintDifferences(access, sqlite, probe, tables));

        using (var transaction = sqlite.BeginTransaction())
        {
            Execute(sqlite, "UPDATE \"Release\" SET Code = Code || ' ' WHERE ReleaseID = (SELECT MIN(ReleaseID) FROM \"Release\")");
            Assert.Contains(NativeOutputChecks.FingerprintDifferences(access, sqlite, probe, tables), d => d.Contains("Release.Code text-length", StringComparison.Ordinal));
            transaction.Rollback();
        }

        using (var transaction = sqlite.BeginTransaction())
        {
            Execute(sqlite, "UPDATE \"Release\" SET IsCurrent = 1 - IsCurrent WHERE ReleaseID = (SELECT MIN(ReleaseID) FROM \"Release\")");
            Assert.Contains(NativeOutputChecks.FingerprintDifferences(access, sqlite, probe, tables), d => d.Contains("Release.IsCurrent true-count", StringComparison.Ordinal));
            transaction.Rollback();
        }

        AccessSqliteComparer.AssertNone(NativeOutputChecks.FingerprintDifferences(access, sqlite, probe, tables));
    }

    [DataFact]
    public void TextOutsideTheBmp_IsPreservedExactly()
    {
        RequireConverted();
        using var access = OpenAccess(fixture.SourcePath);
        using var sqlite = AccessSqliteComparer.OpenSqlite(fixture.OutputPath);

        // Item.Name holds a value with supplementary-plane characters (4.4: four of them in one row).
        var found = new List<(long Id, string Name)>();
        using (var command = new OleDbCommand("SELECT [ItemID], [Name] FROM [Item] WHERE [Name] IS NOT NULL", access))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var name = reader.GetString(1);
                if (name.Any(char.IsSurrogate))
                {
                    found.Add((Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), name));
                }
            }
        }

        Assert.NotEmpty(found);
        foreach (var (id, name) in found)
        {
            var actual = AccessSqliteComparer.ScalarText(sqlite, $"SELECT \"Name\" FROM \"Item\" WHERE \"ItemID\" = {id}");
            Assert.Equal(name, actual);
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(name), System.Text.Encoding.UTF8.GetBytes(actual!));
        }

        // Positive control: a lossy copy (each supplementary character replaced by '?') is not equal.
        var lossy = new string(found[0].Name.Select(c => char.IsSurrogate(c) ? '?' : c).ToArray());
        Assert.NotEqual(found[0].Name, lossy);
    }

    [DataFact]
    public void GuidAndBooleanColumns_HaveTheDocumentedForm_InEveryRow()
    {
        RequireConverted();
        using var access = OpenAccess(fixture.SourcePath);
        using var sqlite = AccessSqliteComparer.OpenSqlite(fixture.OutputPath);
        var probe = AccessProbe.Read(access);
        var tables = NativeOutputChecks.Exported(probe);

        // The documented census (51 Replication ID and 46 Yes/No columns) is over the 70 user tables of the database;
        // 1 and 7 of them are in excluded tables, so the exported tables hold 50 and 39.
        Assert.Equal(51, probe.Columns.Count(c => c.DataType == 72));
        Assert.Equal(46, probe.Columns.Count(c => c.DataType == 11));
        Assert.Equal(50, probe.Columns.Count(c => tables.Contains(c.Table) && c.DataType == 72));
        Assert.Equal(39, probe.Columns.Count(c => tables.Contains(c.Table) && c.DataType == 11));
        AccessSqliteComparer.AssertNone(NativeOutputChecks.GuidFormatDifferences(sqlite, probe, tables));
        AccessSqliteComparer.AssertNone(NativeOutputChecks.BooleanFormatDifferences(sqlite, probe, tables));
    }

    [DataFact]
    public void FormatChecks_PositiveControl_UpperCaseBracedGuidAndBooleanTwoAreDetected()
    {
        RequireConverted();
        using var access = OpenAccess(fixture.SourcePath);
        using var sqlite = AccessSqliteComparer.OpenSqlite(fixture.OutputPath, readOnly: false);
        var probe = AccessProbe.Read(access);
        string[] tables = ["Release"];
        const string first = "WHERE ReleaseID = (SELECT MIN(ReleaseID) FROM \"Release\")";

        using (var transaction = sqlite.BeginTransaction())
        {
            Execute(sqlite, $"UPDATE \"Release\" SET RowGUID = upper(RowGUID) {first}");
            Assert.NotEmpty(NativeOutputChecks.GuidFormatDifferences(sqlite, probe, tables));
            Execute(sqlite, $"UPDATE \"Release\" SET RowGUID = '{{' || lower(RowGUID) || '}}' {first}");
            Assert.NotEmpty(NativeOutputChecks.GuidFormatDifferences(sqlite, probe, tables));
            Execute(sqlite, $"UPDATE \"Release\" SET IsCurrent = 2 {first}");
            Assert.NotEmpty(NativeOutputChecks.BooleanFormatDifferences(sqlite, probe, tables));
            transaction.Rollback();
        }

        AccessSqliteComparer.AssertNone(NativeOutputChecks.GuidFormatDifferences(sqlite, probe, tables));
        AccessSqliteComparer.AssertNone(NativeOutputChecks.BooleanFormatDifferences(sqlite, probe, tables));
    }

    [DataFact]
    public void ForeignKeysAndIndexes_AreThoseOfTheAccessCatalogue()
    {
        RequireConverted();
        using var access = OpenAccess(fixture.SourcePath);
        using var sqlite = AccessSqliteComparer.OpenSqlite(fixture.OutputPath);
        var probe = AccessProbe.Read(access);
        var tables = NativeOutputChecks.Exported(probe);

        AccessSqliteComparer.AssertNone(NativeOutputChecks.ForeignKeyDifferences(sqlite, probe, tables));
        AccessSqliteComparer.AssertNone(NativeOutputChecks.IndexDifferences(sqlite, probe, tables));

        // Primary keys: the same ones Access declares, in the same order.
        foreach (var table in tables)
        {
            var key = AccessSqliteComparer.SqliteColumns(sqlite, table).Where(c => c.Pk > 0).OrderBy(c => c.Pk).Select(c => c.Name).ToList();
            Assert.Equal(probe.PrimaryKeys.GetValueOrDefault(table) ?? [], key);
        }

        // The documentation says 19 composite keys; Access declares 18 among the exported tables (21 over the 70
        // user tables). This assertion pins the SQLite output to Access, not to the documented figure.
        Assert.Equal(
            tables.Count(t => probe.PrimaryKeys.GetValueOrDefault(t)?.Count > 1),
            tables.Count(t => AccessSqliteComparer.SqliteColumns(sqlite, t).Count(c => c.Pk > 0) > 1));
    }

    [DataFact]
    public void ForeignKeysAndIndexes_PositiveControl_AMissingForeignKeyAndIndexAreDetected()
    {
        RequireConverted();
        using var access = OpenAccess(fixture.SourcePath);
        var copy = Path.Join(Path.GetDirectoryName(fixture.OutputPath)!, "schema-control.db");
        var probe = AccessProbe.Read(access);
        var tables = NativeOutputChecks.Exported(probe);

        var catalog = NativeBaseline.Load().Catalog;
        var victim = catalog.ForeignKeys.First(f => tables.Contains(f.ChildTable) && tables.Contains(f.ParentTable));
        // An index with column list not shared by another Access index of its table, so its absence cannot be masked.
        var firstIndex = catalog.Indexes.First(i => tables.Contains(i.Table)
            && !catalog.Indexes.Any(o => o != i && o.Table == i.Table && o.Columns.SequenceEqual(i.Columns))
            && !catalog.Tables.First(t => t.Name == i.Table).PrimaryKey.SequenceEqual(i.Columns));
        var tampered = catalog with
        {
            ForeignKeys = [.. catalog.ForeignKeys.Where(f => f != victim)],
            Indexes = [.. catalog.Indexes.Where(i => i != firstIndex)],
        };
        using (var create = new SqliteConnection($"Data Source={copy};Pooling=False"))
        {
            create.Open();
            foreach (var statement in NativeSchemaBuilder.Build(tampered).Statements)
            {
                Execute(create, statement);
            }
        }

        using var control = AccessSqliteComparer.OpenSqlite(copy);
        Assert.Contains(NativeOutputChecks.ForeignKeyDifferences(control, probe, tables), d => d.StartsWith(victim.ChildTable + ":", StringComparison.Ordinal));
        // The FK child index the builder adds can stand in for a missing Access index only when it has the same
        // columns; the chosen index is not such a case unless it is a foreign key child column, so accept either
        // a reported difference or a covering FK index that SQLite really has.
        var indexDifferences = NativeOutputChecks.IndexDifferences(control, probe, tables);
        var covered = catalog.ForeignKeys.Any(f => f.ChildTable == firstIndex.Table && f.ChildColumns.SequenceEqual(firstIndex.Columns));
        Assert.True(
            covered || indexDifferences.Any(d => d.StartsWith(firstIndex.Table + "." + firstIndex.Name, StringComparison.Ordinal)),
            "The comparison did not notice a removed Access index: " + string.Join("; ", indexDifferences.Take(5)));
    }

    [DataFact]
    public void Output_PassesSqliteIntegrityCheck_AndItsSchemaIsTheBaselineSchema()
    {
        RequireConverted();
        using var sqlite = AccessSqliteComparer.OpenSqlite(fixture.OutputPath);

        Assert.Equal("ok", AccessSqliteComparer.ScalarText(sqlite, "PRAGMA integrity_check"));

        var baselineSchema = NativeSchemaBuilder.Build(NativeBaseline.Load().Catalog).Statements.Order(StringComparer.Ordinal).ToList();
        var actual = AccessSqliteComparer.Rows(sqlite, "SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY sql")
            .Select(r => (string)r[0]!).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(baselineSchema, actual);
    }

    [Fact]
    public void IntegrityCheck_PositiveControl_ACorruptedFileIsNotOk()
    {
        var path = Path.Join(Path.GetTempPath(), $"corrupt_{Environment.ProcessId}_{Guid.NewGuid():N}.db");
        try
        {
            using (var create = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                create.Open();
                Execute(create, "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT); CREATE INDEX t_v ON t (v)");
                using var transaction = create.BeginTransaction();
                for (var i = 0; i < 3000; i++)
                {
                    Execute(create, $"INSERT INTO t (v) VALUES ('value number {i} with some padding to fill the pages')");
                }

                transaction.Commit();
            }

            SqliteConnection.ClearAllPools();
            var bytes = File.ReadAllBytes(path);
            Array.Fill(bytes, (byte)0xAB, bytes.Length / 2, 4096);
            File.WriteAllBytes(path, bytes);

            string? verdict;
            try
            {
                using var corrupted = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
                corrupted.Open();
                verdict = AccessSqliteComparer.ScalarText(corrupted, "PRAGMA integrity_check");
            }
            catch (SqliteException ex)
            {
                verdict = "error: " + ex.Message;
            }

            Assert.NotEqual("ok", verdict);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------------ older publications

    [DataTheory]
    [InlineData(RepoPaths.AccessDpm20Release43DatabaseFileName, "4.3")]
    [InlineData(RepoPaths.AccessDpm20DatabaseFileName, "4.2.1")]
    public void OlderPublications_AreRejected_WithTheBaselineMessage_NamingTheirCurrentRelease(string fileName, string currentRelease)
    {
        var source = Path.Join(RepoPaths.DataDirectory, fileName);
        Assert.True(File.Exists(source), $"Missing data file {source}");
        var output = Path.Join(Path.GetTempPath(), $"native-rejected_{Guid.NewGuid():N}.db");

        // The premise, read from Access itself: no release 4.4, and the expected current release.
        using (var access = OpenAccess(source))
        {
            using var count = new OleDbCommand("SELECT COUNT(1) FROM [Release] WHERE [Code] = '4.4'", access);
            Assert.Equal(0, Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture));
            using var current = new OleDbCommand("SELECT [Code] FROM [Release] WHERE [IsCurrent] = True", access);
            Assert.Equal(currentRelease, current.ExecuteScalar());
        }

        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var code = CliRunner.Run(["--source", source, "--output", output, "--target", "native", "--plain"], stdout, stderr);

            Assert.Equal(CliRunner.ExitArgumentError, code);
            Assert.Contains("4.4", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains($"'{currentRelease}'", stderr.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(output));
            Assert.False(File.Exists(output + ".partial"));
        }
        finally
        {
            OleDbConnection.ReleaseObjectPool();
            File.Delete(output);
        }
    }

    [DataFact]
    public void Gate_PositiveControl_The44DatabaseHasRelease44_SoItIsAccepted()
    {
        RequireConverted();
        using var access = OpenAccess(fixture.SourcePath);
        using var count = new OleDbCommand("SELECT COUNT(1) FROM [Release] WHERE [Code] = '4.4'", access);

        Assert.Equal(1, Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture));
        Assert.Equal(CliRunner.ExitOk, fixture.ExitCode);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
