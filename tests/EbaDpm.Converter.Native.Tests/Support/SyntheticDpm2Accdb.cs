using System.Data.OleDb;
using System.Globalization;

namespace EbaDpm.Converter.Native.Tests.Support;

/// <summary>
/// A small, REAL DPM 2.0-shaped <c>.accdb</c> (it has <c>Category</c> and no <c>Domain</c>, a
/// <c>Release</c> table with a configurable set of codes) built with ACE DDL, so that the native
/// conversion can be exercised end to end without the EBA data files. Every Access type the type
/// mapping covers is present in <c>TypeProbe</c>; the 11 excluded tables, two look-alike names that
/// must still be exported, relationships (cascade and plain) and keyword table names are present too.
/// </summary>
internal sealed class SyntheticDpm2Accdb : IDisposable
{
    public const string TypeProbeLongText = "line one\r\nline two  \t with   trailing spaces   ";
    public const string TypeProbeShortText = "  padded text  ";
    public const string TypeProbeDecimalLong = "123456789.1234567891";
    public const string TypeProbeGuidUpperBraced = "{0E40D86D-889C-498E-AE66-46398E615CEE}";
    public const string TypeProbeGuidExpected = "0e40d86d-889c-498e-ae66-46398e615cee";

    public static readonly string[] ExcludedNames =
    [
        "ChangeLog", "ChangeLogAttribute", "User", "Role", "UserRole",
        "Aux_CellMapping", "Aux_CellStatus", "ModelViolations", "VarGeneration_Detail", "VarGeneration_Summary", "VariableGeneration",
    ];

    private static readonly string[] Ddl =
    [
        "CREATE TABLE [Category] ([CategoryID] LONG NOT NULL, [Code] TEXT(50), CONSTRAINT [pk_Category] PRIMARY KEY ([CategoryID]))",
        "CREATE TABLE [Release] ([ReleaseID] LONG NOT NULL, [Code] TEXT(50) NOT NULL, [IsCurrent] YESNO, CONSTRAINT [pk_Release] PRIMARY KEY ([ReleaseID]))",
        "CREATE TABLE [Table] ([TableID] LONG NOT NULL, [Code] TEXT(50), [StartReleaseID] LONG NOT NULL, [EndReleaseID] LONG, CONSTRAINT [pk_Table] PRIMARY KEY ([TableID]))",
        "CREATE TABLE [Reference] ([ReferenceID] LONG NOT NULL, [TableID] LONG NOT NULL, [Note] TEXT(100), CONSTRAINT [pk_Reference] PRIMARY KEY ([ReferenceID]))",
        "CREATE TABLE [Composite] ([A] LONG NOT NULL, [B] LONG NOT NULL, [Label] TEXT(20), CONSTRAINT [pk_Composite] PRIMARY KEY ([A], [B]))",
        "CREATE TABLE [TypeProbe] ([ProbeID] LONG NOT NULL, [AsLong] LONG, [AsSmall] SHORT, [AsByte] BYTE, [AsBool] YESNO, "
            + "[AsShortText] TEXT(255), [AsLongText] MEMO, [RowGUID] GUID, [AsDecimal] DECIMAL(28,10), [AsDate] DATETIME, "
            + "CONSTRAINT [pk_TypeProbe] PRIMARY KEY ([ProbeID]))",
        "CREATE TABLE [EmptyTable] ([EmptyID] LONG NOT NULL, [Name] TEXT(10), CONSTRAINT [pk_EmptyTable] PRIMARY KEY ([EmptyID]))",
        "CREATE TABLE [ChangeLogX] ([ChangeLogXID] LONG NOT NULL, [Note] TEXT(20), CONSTRAINT [pk_ChangeLogX] PRIMARY KEY ([ChangeLogXID]))",
        "CREATE TABLE [Aux_Other] ([AuxID] LONG NOT NULL, [Note] TEXT(20), CONSTRAINT [pk_Aux_Other] PRIMARY KEY ([AuxID]))",
        "CREATE TABLE [ChangeLog] ([ChangeLogID] LONG NOT NULL, [ReleaseID] LONG, [Note] TEXT(20), CONSTRAINT [pk_ChangeLog] PRIMARY KEY ([ChangeLogID]))",
        "CREATE TABLE [ChangeLogAttribute] ([AttributeID] LONG NOT NULL, [ChangeLogID] LONG NOT NULL, CONSTRAINT [pk_ChangeLogAttribute] PRIMARY KEY ([AttributeID]))",
        "CREATE TABLE [User] ([UserID] LONG NOT NULL, [Name] TEXT(20), CONSTRAINT [pk_User] PRIMARY KEY ([UserID]))",
        "CREATE TABLE [Role] ([RoleID] LONG NOT NULL, [Name] TEXT(20), CONSTRAINT [pk_Role] PRIMARY KEY ([RoleID]))",
        "CREATE TABLE [UserRole] ([UserID] LONG NOT NULL, [RoleID] LONG NOT NULL, CONSTRAINT [pk_UserRole] PRIMARY KEY ([UserID], [RoleID]))",
        "CREATE TABLE [Aux_CellMapping] ([Id] LONG NOT NULL, CONSTRAINT [pk_Aux_CellMapping] PRIMARY KEY ([Id]))",
        "CREATE TABLE [Aux_CellStatus] ([Id] LONG NOT NULL, CONSTRAINT [pk_Aux_CellStatus] PRIMARY KEY ([Id]))",
        "CREATE TABLE [ModelViolations] ([Id] LONG NOT NULL, CONSTRAINT [pk_ModelViolations] PRIMARY KEY ([Id]))",
        "CREATE TABLE [VarGeneration_Detail] ([Id] LONG NOT NULL, CONSTRAINT [pk_VarGeneration_Detail] PRIMARY KEY ([Id]))",
        "CREATE TABLE [VarGeneration_Summary] ([Id] LONG NOT NULL, CONSTRAINT [pk_VarGeneration_Summary] PRIMARY KEY ([Id]))",
        "CREATE TABLE [VariableGeneration] ([Id] LONG NOT NULL, [TableID] LONG, CONSTRAINT [pk_VariableGeneration] PRIMARY KEY ([Id]))",

        "ALTER TABLE [Table] ADD CONSTRAINT [fk_Table_Start] FOREIGN KEY ([StartReleaseID]) REFERENCES [Release] ([ReleaseID]) ON UPDATE CASCADE",
        "ALTER TABLE [Reference] ADD CONSTRAINT [fk_Reference_Table] FOREIGN KEY ([TableID]) REFERENCES [Table] ([TableID]) ON UPDATE CASCADE ON DELETE CASCADE",
        "ALTER TABLE [ChangeLog] ADD CONSTRAINT [fk_ChangeLog_Release] FOREIGN KEY ([ReleaseID]) REFERENCES [Release] ([ReleaseID]) ON UPDATE CASCADE",
        "ALTER TABLE [ChangeLogAttribute] ADD CONSTRAINT [fk_Attr_Log] FOREIGN KEY ([ChangeLogID]) REFERENCES [ChangeLog] ([ChangeLogID]) ON UPDATE CASCADE",
        "ALTER TABLE [VariableGeneration] ADD CONSTRAINT [fk_VarGen_Table] FOREIGN KEY ([TableID]) REFERENCES [Table] ([TableID]) ON UPDATE CASCADE",

        "CREATE UNIQUE INDEX [uq_Table_Code] ON [Table] ([Code])",
        "CREATE INDEX [ix_Composite_BA] ON [Composite] ([B], [A])",
    ];

    private SyntheticDpm2Accdb(string path) => Path = path;

    public string Path { get; }

    /// <summary>Connection string without OLE DB session pooling, so that closing a connection releases the file lock.</summary>
    public static string ConnectionString(string path) => $"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={path};OLE DB Services=-4;";

    /// <summary>
    /// Creates the database. <paramref name="releaseCodes"/> fills <c>Release</c> (the last one is
    /// flagged current). <paramref name="extra"/> runs against the open connection before it closes.
    /// </summary>
    public static SyntheticDpm2Accdb Create(string[]? releaseCodes = null, Action<OleDbConnection>? extra = null)
    {
        releaseCodes ??= ["4.3", "4.4"];
        var path = Dpm2TestAccdbFactory.CreateEmptyAccdb();
        try
        {
            using var connection = new OleDbConnection(ConnectionString(path));
            connection.Open();
            foreach (var statement in Ddl)
            {
                Execute(connection, statement);
            }

            for (var i = 0; i < releaseCodes.Length; i++)
            {
                Execute(
                    connection,
                    "INSERT INTO [Release] ([ReleaseID], [Code], [IsCurrent]) VALUES (?, ?, ?)",
                    i + 1, releaseCodes[i], i == releaseCodes.Length - 1);
            }

            Execute(connection, "INSERT INTO [Category] ([CategoryID], [Code]) VALUES (1, 'c1')");
            SeedData(connection);
            extra?.Invoke(connection);
        }
        finally
        {
            OleDbConnection.ReleaseObjectPool();
        }

        return new SyntheticDpm2Accdb(path);
    }

    public void Dispose()
    {
        OleDbConnection.ReleaseObjectPool();
        foreach (var candidate in new[] { Path, System.IO.Path.ChangeExtension(Path, ".laccdb") })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
            catch (IOException)
            {
                // A temp file that is still locked is left for the OS temp cleanup.
            }
        }
    }

    public static void Execute(OleDbConnection connection, string sql, params object[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var value in parameters)
        {
            command.Parameters.Add(new OleDbParameter { Value = value });
        }

        command.ExecuteNonQuery();
    }

    private static void SeedData(OleDbConnection c)
    {
        Execute(c, "INSERT INTO [Table] ([TableID], [Code], [StartReleaseID], [EndReleaseID]) VALUES (10, 'T_A', 1, NULL)");
        Execute(c, "INSERT INTO [Table] ([TableID], [Code], [StartReleaseID], [EndReleaseID]) VALUES (20, 'T_B', 2, NULL)");
        Execute(c, "INSERT INTO [Reference] ([ReferenceID], [TableID], [Note]) VALUES (1, 10, 'first')");
        Execute(c, "INSERT INTO [Reference] ([ReferenceID], [TableID], [Note]) VALUES (2, 20, NULL)");
        Execute(c, "INSERT INTO [Composite] ([A], [B], [Label]) VALUES (2, 1, 'b')");
        Execute(c, "INSERT INTO [Composite] ([A], [B], [Label]) VALUES (1, 2, 'a')");
        Execute(c, "INSERT INTO [ChangeLogX] ([ChangeLogXID], [Note]) VALUES (1, 'kept')");
        Execute(c, "INSERT INTO [Aux_Other] ([AuxID], [Note]) VALUES (1, 'kept')");
        Execute(c, "INSERT INTO [ChangeLog] ([ChangeLogID], [ReleaseID], [Note]) VALUES (1, 1, 'dropped')");
        Execute(c, "INSERT INTO [User] ([UserID], [Name]) VALUES (1, 'dropped')");
        Execute(c, "INSERT INTO [Aux_CellMapping] ([Id]) VALUES (1)");

        // Row 1: every type filled. Row 2: every column NULL. Rows 3 and 4: boundary values.
        using (var command = c.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO [TypeProbe] ([ProbeID], [AsLong], [AsSmall], [AsByte], [AsBool], [AsShortText], [AsLongText], [RowGUID], [AsDecimal], [AsDate]) "
                + "VALUES (?, ?, ?, ?, ?, ?, ?, ?, DECIMALLITERAL, ?)";
            AddRow(command, 1, int.MaxValue, short.MinValue, 255, true, TypeProbeShortText, TypeProbeLongText,
                TypeProbeGuidUpperBraced, decimal.Parse(TypeProbeDecimalLong, CultureInfo.InvariantCulture),
                new DateTime(2026, 11, 25, 13, 45, 59));
            Run(command, TypeProbeDecimalLong);
            AddRow(command, 3, int.MinValue, short.MaxValue, 0, false, string.Empty, string.Empty,
                "{00000000-0000-0000-0000-000000000001}", 0.1m, new DateTime(1999, 1, 2, 0, 0, 0));
            Run(command, "0.1");
            AddRow(command, 4, 7, 0, 1, true, "x", "y",
                "{FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF}", 12m, new DateTime(2000, 2, 29, 23, 59, 59));
            Run(command, "12");
        }

        Execute(c, "INSERT INTO [TypeProbe] ([ProbeID]) VALUES (2)");
    }

    private static void Run(OleDbCommand command, string decimalLiteral)
    {
        // The decimal is a literal in the SQL text: ACE rejects a Numeric parameter whose precision it cannot match.
        command.CommandText = command.CommandText.Replace("DECIMALLITERAL", decimalLiteral, StringComparison.Ordinal);
        try
        {
            command.ExecuteNonQuery();
        }
        finally
        {
            command.CommandText = command.CommandText.Replace(decimalLiteral, "DECIMALLITERAL", StringComparison.Ordinal);
        }
    }

    private static void AddRow(
        OleDbCommand command, int id, int i, short s, byte b, bool yn, string st, string lt, string guid, decimal dec, DateTime date)
    {
        command.Parameters.Clear();
        command.Parameters.Add(new OleDbParameter { Value = id });
        command.Parameters.Add(new OleDbParameter { Value = i });
        command.Parameters.Add(new OleDbParameter { Value = s });
        command.Parameters.Add(new OleDbParameter { Value = b });
        command.Parameters.Add(new OleDbParameter { Value = yn });
        command.Parameters.Add(new OleDbParameter { Value = st });
        command.Parameters.Add(new OleDbParameter { OleDbType = OleDbType.LongVarWChar, Value = lt });
        command.Parameters.Add(new OleDbParameter { OleDbType = OleDbType.Guid, Value = Guid.Parse(guid) });
        command.Parameters.Add(new OleDbParameter { OleDbType = OleDbType.Date, Value = date });
    }
}
