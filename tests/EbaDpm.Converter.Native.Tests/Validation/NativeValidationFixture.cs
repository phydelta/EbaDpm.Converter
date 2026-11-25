using System.Data.OleDb;
using EbaDpm.Converter.Native.Conversion;
using EbaDpm.Converter.Native.Tests.Support;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.Validation;

/// <summary>
/// One synthetic DPM 2.0 source and its native conversion, shared by the validation tests. On top of
/// <see cref="SyntheticDpm2Accdb"/> it adds the three things the validation tests need: a table named
/// <c>TableVersionCell</c> (so the output is recognised as a native output), a table whose primary key
/// is a Replication ID (the content comparison goes through a dictionary instead of a merge) and a
/// table without a primary key (compared by the whole row), and an extra column on <c>Category</c>.
/// Does nothing when the Access Database Engine is not installed (the tests are <c>[AceFact]</c>).
/// </summary>
public sealed class NativeValidationFixture : IDisposable
{
    public const string GuidOne = "{11111111-1111-1111-1111-111111111111}";
    public const string GuidTwo = "{22222222-2222-2222-2222-222222222222}";

    private readonly string _directory = Path.Join(Path.GetTempPath(), $"native-validate_{Environment.ProcessId}_{Guid.NewGuid():N}");
    private int _copies;

    internal SyntheticDpm2Accdb? Source { get; private set; }

    public string OutputPath => Path.Join(_directory, "out.db");

    public string SourcePath => Source?.Path ?? throw new InvalidOperationException("The Access Database Engine is not available.");

    public string WorkDirectory => _directory;

    public NativeValidationFixture()
    {
        if (!AceRequirement.IsAvailable)
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        Source = SyntheticDpm2Accdb.Create(extra: AddValidationTables);
        NativeConverter.Convert(Source.Path, OutputPath, overwrite: false);
        ReleasePools();
    }

    private static void AddValidationTables(OleDbConnection c)
    {
        SyntheticDpm2Accdb.Execute(c, "CREATE TABLE [TableVersionCell] ([CellID] LONG NOT NULL, [CellCode] LONG, [Note] TEXT(10), CONSTRAINT [pk_TableVersionCell] PRIMARY KEY ([CellID]))");
        SyntheticDpm2Accdb.Execute(c, "INSERT INTO [TableVersionCell] ([CellID], [CellCode], [Note]) VALUES (1, 7, 'cell')");

        SyntheticDpm2Accdb.Execute(c, "CREATE TABLE [GuidKeyed] ([GuidID] GUID NOT NULL, [Name] TEXT(20), CONSTRAINT [pk_GuidKeyed] PRIMARY KEY ([GuidID]))");
        foreach (var (guid, name) in new[] { (GuidOne, "one"), (GuidTwo, "two") })
        {
            using var command = c.CreateCommand();
            command.CommandText = "INSERT INTO [GuidKeyed] ([GuidID], [Name]) VALUES (?, ?)";
            command.Parameters.Add(new OleDbParameter { OleDbType = OleDbType.Guid, Value = Guid.Parse(guid) });
            command.Parameters.Add(new OleDbParameter { Value = name });
            command.ExecuteNonQuery();
        }

        SyntheticDpm2Accdb.Execute(c, "CREATE TABLE [NoKey] ([Tag] TEXT(10), [Val] LONG)");
        SyntheticDpm2Accdb.Execute(c, "INSERT INTO [NoKey] ([Tag], [Val]) VALUES ('a', 1)");
        SyntheticDpm2Accdb.Execute(c, "INSERT INTO [NoKey] ([Tag], [Val]) VALUES ('b', 2)");

        SyntheticDpm2Accdb.Execute(c, "ALTER TABLE [Category] ADD COLUMN [ExtraCol] TEXT(10)");
    }

    /// <summary>
    /// Releases the pooled Access and SQLite connections of this process: ACE keeps the .accdb open after a
    /// connection is closed, and the validator hashes the source before opening it.
    /// </summary>
    public static void ReleasePools()
    {
        OleDbConnection.ReleaseObjectPool();
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    /// <summary>A copy of the converted output with the statements applied; the shared output is never touched.</summary>
    public string Tamper(params string[] statements)
    {
        var path = Path.Join(_directory, $"tampered_{++_copies}.db");
        File.Copy(OutputPath, path, overwrite: true);
        using (var connection = AccessSqliteComparer.OpenSqlite(path, readOnly: false))
        {
            foreach (var sql in statements)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Source?.Dispose();
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Left for the OS temp cleanup.
        }
    }
}
