using System.Data.OleDb;
using System.Globalization;
using System.Security.Cryptography;
using EbaDpm.Converter.Native.Catalog;
using EbaDpm.Converter.Native.Conversion;
using EbaDpm.Converter.Native.Schema;
using EbaDpm.Converter.Native.Tests.Support;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.Conversion;

/// <summary>
/// The native conversion end to end on a synthetic DPM 2.0 Access database (no EBA data files):
/// tables, exclusions, types, values, keys, and the gate and file-handling rules.
/// </summary>
public sealed class NativeConverterSyntheticTests : IClassFixture<SyntheticConversionFixture>, IDisposable
{
    private static readonly string[] ExpectedExported =
        ["Aux_Other", "Category", "ChangeLogX", "Composite", "EmptyTable", "Reference", "Release", "Table", "TypeProbe"];

    private readonly SyntheticConversionFixture _fixture;
    private readonly string _scratch = Path.Join(Path.GetTempPath(), $"native-synth-test_{Environment.ProcessId}_{Guid.NewGuid():N}");

    public NativeConverterSyntheticTests(SyntheticConversionFixture fixture)
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

    private SqliteConnection OpenOutput() => AccessSqliteComparer.OpenSqlite(_fixture.OutputPath);

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ------------------------------------------------------------------ tables and exclusions

    [AceFact]
    public void ExportedTables_AreTheSourceUserTablesMinusTheElevenNamed()
    {
        List<string> sourceTables;
        using (var access = new OleDbConnection(SyntheticDpm2Accdb.ConnectionString(_fixture.SourcePath)))
        {
            access.Open();
            sourceTables = AccessSqliteComparer.AccessUserTables(access);
        }

        var expected = sourceTables.Except(SyntheticDpm2Accdb.ExcludedNames, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        using var sqlite = OpenOutput();
        var actual = AccessSqliteComparer.SqliteUserTables(sqlite).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(expected, actual);
        Assert.Equal(ExpectedExported, actual); // independent literal list
        Assert.Equal(SyntheticDpm2Accdb.ExcludedNames.Order(StringComparer.Ordinal), _fixture.Summary!.ExcludedTablesInSource.Order(StringComparer.Ordinal));
    }

    [AceFact]
    public void ExclusionList_IsAClosedList_NotAPattern_LookAlikeTablesAreExported()
    {
        using var sqlite = OpenOutput();
        var tables = AccessSqliteComparer.SqliteUserTables(sqlite);

        Assert.Contains("ChangeLogX", tables);
        Assert.Contains("Aux_Other", tables);
        Assert.Equal(1, AccessSqliteComparer.ScalarLong(sqlite, "SELECT COUNT(1) FROM \"ChangeLogX\""));
        Assert.Equal(1, AccessSqliteComparer.ScalarLong(sqlite, "SELECT COUNT(1) FROM \"Aux_Other\""));
        foreach (var excluded in SyntheticDpm2Accdb.ExcludedNames)
        {
            Assert.DoesNotContain(excluded, tables);
        }
    }

    [AceFact]
    public void EmptyTable_IsCreatedEmpty_WithItsColumns()
    {
        using var sqlite = OpenOutput();

        Assert.Equal(0, AccessSqliteComparer.ScalarLong(sqlite, "SELECT COUNT(1) FROM \"EmptyTable\""));
        Assert.Equal(["EmptyID", "Name"], AccessSqliteComparer.SqliteColumns(sqlite, "EmptyTable").Select(c => c.Name));
    }

    [AceFact]
    public void TemporaryAndSystemTables_AreNotExported()
    {
        // A table whose name starts with '~' (an Access temporary table) is created in the source.
        using var source = SyntheticDpm2Accdb.Create(extra: c =>
        {
            SyntheticDpm2Accdb.Execute(c, "CREATE TABLE [~TMPCLP123] ([Id] LONG)");
            SyntheticDpm2Accdb.Execute(c, "INSERT INTO [~TMPCLP123] ([Id]) VALUES (1)");
        });

        // Positive control: the temporary table really is in the source catalogue as a table-like object.
        using (var access = new OleDbConnection(SyntheticDpm2Accdb.ConnectionString(source.Path)))
        {
            access.Open();
            using var schema = access.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, null)!;
            Assert.Contains(schema.Rows.Cast<System.Data.DataRow>(), r => (string)r["TABLE_NAME"] == "~TMPCLP123");
            Assert.Contains(schema.Rows.Cast<System.Data.DataRow>(), r => ((string)r["TABLE_NAME"]).StartsWith("MSys", StringComparison.OrdinalIgnoreCase));
        }

        var output = Path.Join(_scratch, "tmp.db");
        NativeConverter.Convert(source.Path, output, overwrite: false);

        using var sqlite = AccessSqliteComparer.OpenSqlite(output);
        var tables = AccessSqliteComparer.SqliteUserTables(sqlite);
        Assert.DoesNotContain(tables, t => t.StartsWith('~'));
        Assert.DoesNotContain(tables, t => t.StartsWith("MSys", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ExpectedExported, tables.Order(StringComparer.Ordinal));
    }

    // ------------------------------------------------------------------ values and types

    [AceFact]
    public void TypeProbe_ColumnsHaveTheDocumentedSqliteTypes()
    {
        using var sqlite = OpenOutput();

        var types = AccessSqliteComparer.Rows(sqlite, "PRAGMA table_info(\"TypeProbe\")")
            .ToDictionary(r => (string)r[1]!, r => (string)r[2]!);
        Assert.Equal("INTEGER", types["AsLong"]);
        Assert.Equal("INTEGER", types["AsSmall"]);
        Assert.Equal("INTEGER", types["AsByte"]);
        Assert.Equal("INTEGER", types["AsBool"]);
        Assert.Equal("TEXT", types["AsShortText"]);
        Assert.Equal("TEXT", types["AsLongText"]);
        Assert.Equal("TEXT", types["RowGUID"]);
        Assert.Equal("TEXT", types["AsDecimal"]);
        Assert.Equal("TEXT", types["AsDate"]);
    }

    [AceFact]
    public void TypeProbe_Row1_EveryType_ExactValues_AndStorageClasses()
    {
        using var sqlite = OpenOutput();

        var row = Assert.Single(AccessSqliteComparer.Rows(
            sqlite,
            "SELECT AsLong, AsSmall, AsByte, AsBool, AsShortText, AsLongText, RowGUID, AsDecimal, AsDate, "
            + "typeof(AsLong), typeof(AsSmall), typeof(AsByte), typeof(AsBool), typeof(AsShortText), typeof(AsLongText), "
            + "typeof(RowGUID), typeof(AsDecimal), typeof(AsDate) FROM TypeProbe WHERE ProbeID = 1"));

        Assert.Equal(2147483647L, row[0]);
        Assert.Equal(-32768L, row[1]);
        Assert.Equal(255L, row[2]);
        Assert.Equal(1L, row[3]); // true -> 1
        Assert.Equal(SyntheticDpm2Accdb.TypeProbeShortText, row[4]); // not trimmed
        Assert.Equal(SyntheticDpm2Accdb.TypeProbeLongText, row[5]); // long text, not trimmed
        Assert.Equal(SyntheticDpm2Accdb.TypeProbeGuidExpected, row[6]); // lower case, 36 chars, no braces
        Assert.Equal(36, ((string)row[6]!).Length);
        Assert.Equal(SyntheticDpm2Accdb.TypeProbeDecimalLong, row[7]); // exact, as text
        Assert.Equal("2026-11-25T13:45:59", row[8]);
        Assert.Equal(
            ["integer", "integer", "integer", "integer", "text", "text", "text", "text", "text"],
            row.Skip(9).Select(v => (string)v!));
    }

    [AceFact]
    public void TypeProbe_Row2_NothingStored_NullStaysNull_InEveryNullableColumn()
    {
        // Access has no NULL for Yes/No: a row inserted without a value reads False. Ask Access itself.
        using (var access = new OleDbConnection(SyntheticDpm2Accdb.ConnectionString(_fixture.SourcePath)))
        {
            access.Open();
            using var command = new OleDbCommand("SELECT [AsBool] FROM [TypeProbe] WHERE [ProbeID] = 2", access);
            Assert.Equal(false, command.ExecuteScalar());
        }

        using var sqlite = OpenOutput();

        var row = Assert.Single(AccessSqliteComparer.Rows(
            sqlite, "SELECT AsLong, AsSmall, AsByte, AsShortText, AsLongText, RowGUID, AsDecimal, AsDate FROM TypeProbe WHERE ProbeID = 2"));
        Assert.All(row, Assert.Null);
        Assert.Equal(0L, AccessSqliteComparer.ScalarLong(sqlite, "SELECT AsBool FROM TypeProbe WHERE ProbeID = 2")); // False -> 0
    }

    [AceFact]
    public void TypeProbe_BoundaryRows_FalseIsZero_EmptyTextIsNotNull_DecimalsAreExact()
    {
        using var sqlite = OpenOutput();

        var row3 = Assert.Single(AccessSqliteComparer.Rows(
            sqlite, "SELECT AsLong, AsSmall, AsByte, AsBool, AsShortText, AsLongText, RowGUID, AsDecimal, AsDate FROM TypeProbe WHERE ProbeID = 3"));
        Assert.Equal(-2147483648L, row3[0]);
        Assert.Equal(32767L, row3[1]);
        Assert.Equal(0L, row3[2]);
        Assert.Equal(0L, row3[3]); // false -> 0, not NULL
        Assert.Equal(string.Empty, row3[4]); // '' stays '' (not NULL)
        Assert.Equal(string.Empty, row3[5]);
        Assert.Equal("00000000-0000-0000-0000-000000000001", row3[6]);
        Assert.Equal("0.1", row3[7]); // 0.1 exactly, as text
        Assert.Equal("1999-01-02T00:00:00", row3[8]);

        var row4 = Assert.Single(AccessSqliteComparer.Rows(
            sqlite, "SELECT RowGUID, AsDecimal, AsDate FROM TypeProbe WHERE ProbeID = 4"));
        Assert.Equal("ffffffff-ffff-ffff-ffff-ffffffffffff", row4[0]);
        Assert.Equal("12", row4[1]);
        Assert.Equal("2000-02-29T23:59:59", row4[2]);
    }

    [AceFact]
    public void EveryTable_RowsIdsAndValues_AreIdenticalToTheSource()
    {
        using var access = new OleDbConnection(SyntheticDpm2Accdb.ConnectionString(_fixture.SourcePath));
        access.Open();
        using var sqlite = OpenOutput();

        var totalCells = 0L;
        var kinds = new HashSet<AccessSqliteComparer.Kind>();
        foreach (var table in _fixture.Summary!.Schema.Tables)
        {
            var result = AccessSqliteComparer.Compare(
                access, sqlite, table.Name, table.Columns.Select(c => c.Name).ToList(), table.PrimaryKey);

            Assert.Empty(result.Differences);
            Assert.Equal(AccessSqliteComparer.AccessCount(access, table.Name), AccessSqliteComparer.ScalarLong(sqlite, $"SELECT COUNT(1) FROM {AccessSqliteComparer.Q(table.Name)}"));
            Assert.Equal(result.RowsCompared, AccessSqliteComparer.ScalarLong(sqlite, $"SELECT COUNT(1) FROM {AccessSqliteComparer.Q(table.Name)}"));
            totalCells += result.CellsCompared;
            foreach (var (kind, count) in result.CellsByKind.Where(p => p.Value > 0))
            {
                kinds.Add(kind);
            }
        }

        // Not vacuous: every value kind of the mapping was compared at least once.
        Assert.True(totalCells > 50, $"Only {totalCells} cells were compared.");
        Assert.Equal(Enum.GetValues<AccessSqliteComparer.Kind>().Order(), kinds.Order());
    }

    [AceFact]
    public void Comparer_PositiveControl_ATamperedCellRowOrCountIsDetected()
    {
        using var access = new OleDbConnection(SyntheticDpm2Accdb.ConnectionString(_fixture.SourcePath));
        access.Open();
        var copy = Path.Join(_scratch, "tampered.db");
        File.Copy(_fixture.OutputPath, copy);
        using var sqlite = AccessSqliteComparer.OpenSqlite(copy, readOnly: false);

        IReadOnlyList<string> Differences(string table)
        {
            var columns = AccessSqliteComparer.SqliteColumns(sqlite, table);
            return AccessSqliteComparer.Compare(
                access, sqlite, table, columns.Select(c => c.Name).ToList(),
                columns.Where(c => c.Pk > 0).OrderBy(c => c.Pk).Select(c => c.Name).ToList()).Differences;
        }

        Assert.Empty(Differences("TypeProbe"));

        void Tamper(string sql)
        {
            using var command = sqlite.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        Tamper("UPDATE TypeProbe SET RowGUID = upper(RowGUID) WHERE ProbeID = 1"); // case of the GUID
        Assert.Contains(Differences("TypeProbe"), d => d.Contains("RowGUID", StringComparison.Ordinal));
        Tamper("UPDATE TypeProbe SET RowGUID = '0e40d86d-889c-498e-ae66-46398e615cee' WHERE ProbeID = 1");
        Assert.Empty(Differences("TypeProbe"));

        Tamper("UPDATE TypeProbe SET AsBool = 2 WHERE ProbeID = 1");
        Assert.Contains(Differences("TypeProbe"), d => d.Contains("AsBool", StringComparison.Ordinal));
        Tamper("UPDATE TypeProbe SET AsBool = 1 WHERE ProbeID = 1");

        Tamper("UPDATE TypeProbe SET AsDecimal = '123456789.1234567890' WHERE ProbeID = 1"); // last digit lost
        Assert.Contains(Differences("TypeProbe"), d => d.Contains("AsDecimal", StringComparison.Ordinal));
        Tamper("UPDATE TypeProbe SET AsDecimal = '123456789.1234567891' WHERE ProbeID = 1");

        Tamper("UPDATE TypeProbe SET AsLongText = rtrim(AsLongText) WHERE ProbeID = 1"); // trimmed
        Assert.Contains(Differences("TypeProbe"), d => d.Contains("AsLongText", StringComparison.Ordinal));
        Tamper("UPDATE TypeProbe SET AsLongText = NULL WHERE ProbeID = 1"); // NULL for a value
        Assert.Contains(Differences("TypeProbe"), d => d.Contains("AsLongText", StringComparison.Ordinal));

        Tamper("DELETE FROM Reference WHERE ReferenceID = 2");
        Assert.Contains(Differences("Reference"), d => d.Contains("row missing", StringComparison.Ordinal));
    }

    [AceFact]
    public void Decimals_PositiveControl_ComparingAsTextCatchesWhatABinaryFloatWouldLose()
    {
        // The stored text, parsed as double and printed back, is NOT the exact value: a column that had
        // been written as REAL would fail the exact-text assertion above.
        var exact = SyntheticDpm2Accdb.TypeProbeDecimalLong;
        var viaDouble = double.Parse(exact, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);

        Assert.NotEqual(exact, viaDouble);
    }

    // ------------------------------------------------------------------ keys and indexes in the output

    [AceFact]
    public void ForeignKeys_AreDeclaredWithTheirRules_AndThoseToExcludedTablesAreNot()
    {
        var summary = _fixture.Summary!;
        using var sqlite = OpenOutput();

        Assert.Equal(["fk_Reference_Table", "fk_Table_Start"], summary.Schema.ForeignKeysDeclared.Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["fk_Attr_Log", "fk_ChangeLog_Release", "fk_VarGen_Table"],
            summary.Schema.ForeignKeysDropped.Select(f => f.Name).Order(StringComparer.Ordinal));

        var reference = Assert.Single(AccessSqliteComparer.Rows(sqlite, "PRAGMA foreign_key_list(\"Reference\")"));
        Assert.Equal(("Table", "TableID", "TableID", "CASCADE", "CASCADE"), ((string)reference[2]!, (string)reference[3]!, (string)reference[4]!, (string)reference[5]!, (string)reference[6]!));
        var table = Assert.Single(AccessSqliteComparer.Rows(sqlite, "PRAGMA foreign_key_list(\"Table\")"));
        Assert.Equal(("Release", "StartReleaseID", "ReleaseID", "CASCADE"), ((string)table[2]!, (string)table[3]!, (string)table[4]!, (string)table[5]!));
        Assert.Empty(AccessSqliteComparer.Rows(sqlite, "PRAGMA foreign_key_check"));
        Assert.Equal("ok", AccessSqliteComparer.ScalarText(sqlite, "PRAGMA integrity_check"));
    }

    [AceFact]
    public void KeywordNamedTables_AreQuotedInTheOutputDdl()
    {
        using var sqlite = OpenOutput();

        foreach (var name in new[] { "Table", "Release", "Reference" })
        {
            var sql = AccessSqliteComparer.ScalarText(sqlite, $"SELECT sql FROM sqlite_master WHERE type = 'table' AND name = '{name}'");
            Assert.StartsWith($"CREATE TABLE \"{name}\" (", sql, StringComparison.Ordinal);
        }
    }

    [AceFact]
    public void Output_CompositePrimaryKey_AndAccessIndexes_AreReproduced()
    {
        using var sqlite = OpenOutput();

        var key = AccessSqliteComparer.SqliteColumns(sqlite, "Composite").Where(c => c.Pk > 0).OrderBy(c => c.Pk).Select(c => c.Name);
        Assert.Equal(["A", "B"], key);

        var indexes = AccessSqliteComparer.Rows(sqlite, "SELECT name FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL")
            .Select(r => (string)r[0]!).ToList();
        Assert.Contains("Table_uq_Table_Code", indexes);
        Assert.Contains("Composite_ix_Composite_BA", indexes);
        var unique = AccessSqliteComparer.Rows(sqlite, "PRAGMA index_list(\"Table\")").Single(r => (string)r[1]! == "Table_uq_Table_Code");
        Assert.Equal(1, Convert.ToInt32(unique[2], CultureInfo.InvariantCulture));
        Assert.Equal(
            ["B", "A"],
            AccessSqliteComparer.Rows(sqlite, "PRAGMA index_info(\"Composite_ix_Composite_BA\")").OrderBy(r => Convert.ToInt32(r[0], CultureInfo.InvariantCulture)).Select(r => (string)r[2]!));
    }

    [AceFact]
    public void Output_HasNoPartialFile_AfterSuccess()
    {
        Assert.True(File.Exists(_fixture.OutputPath));
        Assert.False(File.Exists(_fixture.OutputPath + ".partial"));
    }

    // ------------------------------------------------------------------ the 4.4 gate

    [AceFact]
    public void Gate_SourceWithoutRelease44_IsRejected_NamingItsCurrentRelease_AndLeavesNothing()
    {
        using var source = SyntheticDpm2Accdb.Create(["4.2", "4.3"]);
        var output = Path.Join(_scratch, "gate.db");

        var ex = Assert.Throws<NativeSourceBelowBaselineException>(() => NativeConverter.Convert(source.Path, output, overwrite: false));

        Assert.Equal("4.3", ex.CurrentReleaseCode);
        Assert.Equal("4.4", ex.BaselineReleaseCode);
        Assert.Contains("'4.3'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("4.4", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".partial"));
    }

    [AceFact]
    public void Gate_SourceWithRelease44_IsAccepted_EvenWhenItIsNotTheCurrentOne()
    {
        // A later publication carries 4.4 in its history and is accepted.
        using var source = SyntheticDpm2Accdb.Create(["4.3", "4.4", "4.5"]);
        var output = Path.Join(_scratch, "later.db");

        var summary = NativeConverter.Convert(source.Path, output, overwrite: false);

        Assert.True(File.Exists(output));
        Assert.Equal("4.5", summary.CurrentReleaseCode);
        using var sqlite = AccessSqliteComparer.OpenSqlite(output);
        Assert.Equal(["4.3", "4.4", "4.5"], AccessSqliteComparer.Rows(sqlite, "SELECT Code FROM \"Release\" ORDER BY ReleaseID").Select(r => (string)r[0]!));
    }

    [AceFact]
    public void Gate_ReleaseCodeMustMatchExactly_44IsNot_4_4_1_Nor_44()
    {
        using var source = SyntheticDpm2Accdb.Create(["4.4.1", "44", "4.40", " 4.4x"]);

        Assert.Throws<NativeSourceBelowBaselineException>(
            () => NativeConverter.Convert(source.Path, Path.Join(_scratch, "g2.db"), overwrite: false));
    }

    [AceFact]
    public void Dpm10Source_IsRejected_AndLeavesNothing()
    {
        var path = Dpm2TestAccdbFactory.CreateAccdbWithTables("Domain", "Category", "Release");
        try
        {
            var output = Path.Join(_scratch, "dpm1.db");

            var ex = Assert.Throws<NativeSourceNotDpm20Exception>(() => NativeConverter.Convert(path, output, overwrite: false));

            Assert.Contains("DPM 1.0", ex.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
            Assert.False(File.Exists(output + ".partial"));
        }
        finally
        {
            TempFiles.TryDeleteAccdb(path);
        }
    }

    [AceFact]
    public void UnmappedAccessType_StopsTheConversion_NamingTableAndColumn_AndLeavesNothing()
    {
        using var source = SyntheticDpm2Accdb.Create(extra: c =>
            SyntheticDpm2Accdb.Execute(c, "CREATE TABLE [Money] ([MoneyID] LONG NOT NULL, [Amount] CURRENCY, CONSTRAINT [pk_Money] PRIMARY KEY ([MoneyID]))"));
        var output = Path.Join(_scratch, "money.db");

        var ex = Assert.Throws<NotSupportedException>(() => NativeConverter.Convert(source.Path, output, overwrite: false));

        Assert.Contains("Money.Amount", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".partial"));
    }

    // ------------------------------------------------------------------ file handling

    [AceFact]
    public void FailureDuringTheCopy_LeavesNoOutputAndNoPartial()
    {
        var output = Path.Join(_scratch, "fail.db");
        var copied = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => NativeConverter.Convert(
            _fixture.SourcePath, output, overwrite: false,
            progress: e =>
            {
                if (e.Kind == NativeConversionEventKind.TableCopied && ++copied == 3)
                {
                    throw new InvalidOperationException("injected failure after the partial file exists");
                }
            }));

        Assert.Contains("injected failure", ex.Message, StringComparison.Ordinal);
        Assert.Equal(3, copied);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".partial"));
        Assert.Empty(Directory.GetFiles(_scratch, "fail.db*"));
    }

    [AceFact]
    public void FailureDuringTheCopy_WithOverwrite_LeavesTheExistingOutputUntouched()
    {
        var output = Path.Join(_scratch, "existing.db");
        File.Copy(_fixture.OutputPath, output);
        var before = Hash(output);

        Assert.Throws<InvalidOperationException>(() => NativeConverter.Convert(
            _fixture.SourcePath, output, overwrite: true,
            progress: e =>
            {
                if (e.Kind == NativeConversionEventKind.StageStarted && e.Stage == NativeConversionStage.CreateIndexes)
                {
                    throw new InvalidOperationException("injected failure");
                }
            }));

        Assert.Equal(before, Hash(output));
        Assert.False(File.Exists(output + ".partial"));
    }

    [AceFact]
    public void ExistingOutput_WithoutOverwrite_IsAnError_AndTheFileIsUntouched()
    {
        var output = Path.Join(_scratch, "keep.db");
        File.WriteAllText(output, "precious existing content");
        var before = Hash(output);

        var ex = Assert.Throws<IOException>(() => NativeConverter.Convert(_fixture.SourcePath, output, overwrite: false));

        Assert.Contains("--overwrite", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, Hash(output));
        Assert.Equal("precious existing content", File.ReadAllText(output));
        Assert.False(File.Exists(output + ".partial"));
    }

    [AceFact]
    public void ExistingOutput_WithOverwrite_IsReplacedByTheNewFile()
    {
        var output = Path.Join(_scratch, "replace.db");
        File.WriteAllText(output, "old content that is not a database");

        var summary = NativeConverter.Convert(_fixture.SourcePath, output, overwrite: true);

        Assert.Equal(9, summary.Tables.Count);
        using var sqlite = AccessSqliteComparer.OpenSqlite(output);
        Assert.Equal(ExpectedExported, AccessSqliteComparer.SqliteUserTables(sqlite).Order(StringComparer.Ordinal));
        Assert.Equal("ok", AccessSqliteComparer.ScalarText(sqlite, "PRAGMA integrity_check"));
        Assert.False(File.Exists(output + ".partial"));
    }

    [AceFact]
    public void Output_InAMissingDirectory_CreatesTheDirectory()
    {
        var output = Path.Join(_scratch, "a", "b", "nested.db");

        NativeConverter.Convert(_fixture.SourcePath, output, overwrite: false);

        Assert.True(File.Exists(output));
    }

    [AceFact]
    public void Summary_TablesAndRows_MatchTheOutput()
    {
        var summary = _fixture.Summary!;
        using var sqlite = OpenOutput();

        Assert.Equal(ExpectedExported, summary.Tables.Select(t => t.Name).Order(StringComparer.Ordinal));
        foreach (var result in summary.Tables)
        {
            Assert.Equal(result.Rows, AccessSqliteComparer.ScalarLong(sqlite, $"SELECT COUNT(1) FROM {AccessSqliteComparer.Q(result.Name)}"));
        }

        Assert.Equal("4.4", summary.CurrentReleaseCode);
    }
}
