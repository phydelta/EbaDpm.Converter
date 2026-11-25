using System.Data.OleDb;
using EbaDpm.Converter.Native.Catalog;
using EbaDpm.Converter.Native.Schema;
using EbaDpm.Converter.Native.Tests.Support;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.Conversion;

/// <summary>
/// (1) The catalogue reader on a real synthetic <c>.accdb</c>: tables, types, nullability, keys,
/// relationships and indexes as Access declares them. (2) The independent whole-table checks that
/// the 4.4 data tests rely on, exercised on the synthetic output, with a tampered copy as positive
/// control, so a zero from them on the real data is a result and not an artefact.
/// </summary>
public sealed class NativeCatalogAndChecksSyntheticTests : IClassFixture<SyntheticConversionFixture>, IDisposable
{
    private readonly SyntheticConversionFixture _fixture;
    private readonly string _scratch = Path.Join(Path.GetTempPath(), $"native-checks_{Environment.ProcessId}_{Guid.NewGuid():N}");

    public NativeCatalogAndChecksSyntheticTests(SyntheticConversionFixture fixture)
    {
        _fixture = fixture;
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TempFiles.ReleaseAccess();
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
            // Left for the OS temp cleanup.
        }
    }

    private OleDbConnection OpenAccess()
    {
        var connection = new OleDbConnection(SyntheticDpm2Accdb.ConnectionString(_fixture.SourcePath));
        connection.Open();
        return connection;
    }

    // ------------------------------------------------------------------ catalogue reader

    [AceFact]
    public void CatalogueReader_ReadsEveryUserTable_ButNoSystemOrTemporaryTable()
    {
        using var source = SyntheticDpm2Accdb.Create(extra: c => SyntheticDpm2Accdb.Execute(c, "CREATE TABLE [~TMPCLP9] ([Id] LONG)"));

        var catalog = NativeCatalogReader.Read(source.Path);

        var names = catalog.Tables.Select(t => t.Name).ToList();
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.DoesNotContain(names, n => n.StartsWith('~'));
        Assert.DoesNotContain(names, n => n.StartsWith("MSys", StringComparison.OrdinalIgnoreCase));
        // Complete: the excluded tables are in the catalogue (the exclusion is applied by the builder).
        foreach (var excluded in SyntheticDpm2Accdb.ExcludedNames)
        {
            Assert.Contains(excluded, names);
        }

        Assert.Equal(20, names.Count);
    }

    [AceFact]
    public void CatalogueReader_ReportsTypesNullabilityAndKeys()
    {
        var catalog = NativeCatalogReader.Read(_fixture.SourcePath);

        var probe = catalog.Tables.Single(t => t.Name == "TypeProbe");
        var types = probe.Columns.ToDictionary(c => c.Name, c => c.DataType);
        Assert.Equal(
            ["ProbeID", "AsLong", "AsSmall", "AsByte", "AsBool", "AsShortText", "AsLongText", "RowGUID", "AsDecimal", "AsDate"],
            probe.Columns.Select(c => c.Name));
        Assert.Equal(3, types["AsLong"]);
        Assert.Equal(2, types["AsSmall"]);
        Assert.Equal(17, types["AsByte"]);
        Assert.Equal(11, types["AsBool"]);
        Assert.Equal(130, types["AsShortText"]);
        Assert.Equal(130, types["AsLongText"]); // long text is reported as adWChar too
        Assert.Equal(72, types["RowGUID"]);
        Assert.Equal(131, types["AsDecimal"]);
        Assert.Equal(7, types["AsDate"]);
        Assert.False(probe.Columns.Single(c => c.Name == "ProbeID").Nullable);
        Assert.True(probe.Columns.Single(c => c.Name == "AsLong").Nullable);
        Assert.Equal(["ProbeID"], probe.PrimaryKey);
        Assert.Equal(["A", "B"], catalog.Tables.Single(t => t.Name == "Composite").PrimaryKey);
        Assert.Equal(
            ["ReleaseID", "Code", "IsCurrent"],
            catalog.Tables.Single(t => t.Name == "Release").Columns.Select(c => c.Name));
    }

    [AceFact]
    public void CatalogueReader_ReportsRelationshipsWithTheirRules_AndTheAccessIndexes()
    {
        var catalog = NativeCatalogReader.Read(_fixture.SourcePath);

        var reference = catalog.ForeignKeys.Single(f => f.Name == "fk_Reference_Table");
        Assert.Equal("Reference", reference.ChildTable);
        Assert.Equal(["TableID"], reference.ChildColumns);
        Assert.Equal("Table", reference.ParentTable);
        Assert.Equal(["TableID"], reference.ParentColumns);
        Assert.Equal("CASCADE", reference.UpdateRule);
        Assert.Equal("CASCADE", reference.DeleteRule);
        var start = catalog.ForeignKeys.Single(f => f.Name == "fk_Table_Start");
        Assert.Equal("CASCADE", start.UpdateRule);
        Assert.Equal("NO ACTION", start.DeleteRule);
        Assert.Equal(5, catalog.ForeignKeys.Count);

        var unique = catalog.Indexes.Single(i => i.Name == "uq_Table_Code");
        Assert.True(unique.Unique);
        Assert.Equal(["Code"], unique.Columns);
        var composite = catalog.Indexes.Single(i => i.Name == "ix_Composite_BA");
        Assert.False(composite.Unique);
        Assert.Equal(["B", "A"], composite.Columns);
        Assert.DoesNotContain(catalog.Indexes, i => catalog.Tables.Single(t => t.Name == i.Table).PrimaryKey.SequenceEqual(i.Columns) && i.Name == "pk_Table");
    }

    [AceFact]
    public void CatalogueReader_AndIndependentProbe_AgreeOnTablesColumnsKeysAndRelationships()
    {
        var catalog = NativeCatalogReader.Read(_fixture.SourcePath);
        using var access = OpenAccess();
        var probe = AccessProbe.Read(access);

        Assert.Equal(probe.Tables, catalog.Tables.Select(t => t.Name));
        foreach (var table in catalog.Tables)
        {
            Assert.Equal(probe.ColumnsOf(table.Name).Select(c => (c.Name, c.DataType)), table.Columns.Select(c => (c.Name, c.DataType)));
            Assert.Equal(probe.PrimaryKeys.GetValueOrDefault(table.Name) ?? [], table.PrimaryKey);
        }

        Assert.Equal(
            probe.ForeignKeys.Select(f => f.Name).Order(StringComparer.Ordinal),
            catalog.ForeignKeys.Select(f => f.Name).Order(StringComparer.Ordinal));
    }

    // ------------------------------------------------------------------ the whole-table checks, with positive controls

    [AceFact]
    public void OutputChecks_OnTheUntamperedOutput_ReportNothing_AndTheyDoLookAtData()
    {
        using var access = OpenAccess();
        using var sqlite = AccessSqliteComparer.OpenSqlite(_fixture.OutputPath);
        var probe = AccessProbe.Read(access);
        var tables = NativeOutputChecks.Exported(probe);

        Assert.Equal(9, tables.Length);
        Assert.Empty(NativeOutputChecks.RowCountDifferences(access, sqlite, tables));
        Assert.Empty(NativeOutputChecks.FingerprintDifferences(access, sqlite, probe, tables));
        Assert.Empty(NativeOutputChecks.GuidFormatDifferences(sqlite, probe, tables));
        Assert.Empty(NativeOutputChecks.BooleanFormatDifferences(sqlite, probe, tables));
        Assert.Empty(NativeOutputChecks.ForeignKeyDifferences(sqlite, probe, tables));
        Assert.Empty(NativeOutputChecks.IndexDifferences(sqlite, probe, tables));
    }

    [AceFact]
    public void OutputChecks_PositiveControl_EachCheckReportsATamperedOutput()
    {
        using var access = OpenAccess();
        var probe = AccessProbe.Read(access);
        var tables = NativeOutputChecks.Exported(probe);
        var copy = Path.Join(_scratch, "tampered.db");
        File.Copy(_fixture.OutputPath, copy);
        using var sqlite = AccessSqliteComparer.OpenSqlite(copy, readOnly: false);

        void Execute(string sql)
        {
            using var command = sqlite.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        // Row count.
        Execute("DELETE FROM \"Composite\" WHERE A = 1");
        Assert.Contains(NativeOutputChecks.RowCountDifferences(access, sqlite, tables), d => d.StartsWith("Composite:", StringComparison.Ordinal));

        // Aggregate fingerprints: integer sum, true-count, text length, count of non-null values.
        Execute("UPDATE \"TypeProbe\" SET AsLong = AsLong - 1 WHERE ProbeID = 4");
        Execute("UPDATE \"TypeProbe\" SET AsBool = 0 WHERE ProbeID = 4");
        Execute("UPDATE \"TypeProbe\" SET AsLongText = rtrim(AsLongText) WHERE ProbeID = 1");
        Execute("UPDATE \"TypeProbe\" SET AsDecimal = NULL WHERE ProbeID = 4");
        var fingerprint = NativeOutputChecks.FingerprintDifferences(access, sqlite, probe, tables);
        Assert.Contains(fingerprint, d => d.Contains("TypeProbe.AsLong sum", StringComparison.Ordinal));
        Assert.Contains(fingerprint, d => d.Contains("TypeProbe.AsBool true-count", StringComparison.Ordinal));
        Assert.Contains(fingerprint, d => d.Contains("TypeProbe.AsLongText text-length", StringComparison.Ordinal));
        Assert.Contains(fingerprint, d => d.Contains("TypeProbe.AsDecimal count", StringComparison.Ordinal));

        // Formats.
        Execute("UPDATE \"TypeProbe\" SET RowGUID = upper(RowGUID) WHERE ProbeID = 1");
        Execute("UPDATE \"TypeProbe\" SET AsBool = 7 WHERE ProbeID = 3");
        Assert.NotEmpty(NativeOutputChecks.GuidFormatDifferences(sqlite, probe, tables));
        Assert.NotEmpty(NativeOutputChecks.BooleanFormatDifferences(sqlite, probe, tables));
        Execute("UPDATE \"TypeProbe\" SET RowGUID = '{' || lower(RowGUID) || '}' WHERE ProbeID = 1");
        Assert.NotEmpty(NativeOutputChecks.GuidFormatDifferences(sqlite, probe, tables));

        // Foreign keys and indexes: rebuild a table without them.
        Execute("DROP INDEX \"Table_uq_Table_Code\"; DROP INDEX \"Composite_ix_Composite_BA\"");
        Assert.Contains(NativeOutputChecks.IndexDifferences(sqlite, probe, tables), d => d.Contains("uq_Table_Code", StringComparison.Ordinal));
        Assert.Contains(NativeOutputChecks.IndexDifferences(sqlite, probe, tables), d => d.Contains("ix_Composite_BA", StringComparison.Ordinal));

        var noForeignKeys = Path.Join(_scratch, "nofk.db");
        using (var create = new SqliteConnection($"Data Source={noForeignKeys};Pooling=False"))
        {
            create.Open();
            using var command = create.CreateCommand();
            command.CommandText = "CREATE TABLE \"Reference\" (\"ReferenceID\" INTEGER NOT NULL, \"TableID\" INTEGER NOT NULL, \"Note\" TEXT, PRIMARY KEY (\"ReferenceID\")); "
                + "CREATE TABLE \"Table\" (\"TableID\" INTEGER NOT NULL, \"Code\" TEXT, \"StartReleaseID\" INTEGER NOT NULL, \"EndReleaseID\" INTEGER, PRIMARY KEY (\"TableID\"))";
            command.ExecuteNonQuery();
        }

        using var bare = AccessSqliteComparer.OpenSqlite(noForeignKeys);
        var fkDifferences = NativeOutputChecks.ForeignKeyDifferences(bare, probe, ["Reference", "Table"]);
        Assert.Contains(fkDifferences, d => d.StartsWith("Reference:", StringComparison.Ordinal));
        Assert.DoesNotContain(fkDifferences, d => d.StartsWith("Table:", StringComparison.Ordinal)); // its parent (Release) is not in this table set
    }

    [AceFact]
    public void OutputChecks_ForeignKeys_ToAnExcludedTable_AreNotExpected()
    {
        using var access = OpenAccess();
        using var sqlite = AccessSqliteComparer.OpenSqlite(_fixture.OutputPath);
        var probe = AccessProbe.Read(access);

        // fk_VarGen_Table, fk_ChangeLog_Release and fk_Attr_Log touch an excluded table: the check expects none of them.
        Assert.Contains(probe.ForeignKeys, f => f.Name == "fk_VarGen_Table");
        Assert.Empty(NativeOutputChecks.ForeignKeyDifferences(sqlite, probe, NativeOutputChecks.Exported(probe)));
        Assert.All(NativeOutputChecks.Exported(probe), t => Assert.False(NativeExclusions.IsExcluded(t)));
    }
}
