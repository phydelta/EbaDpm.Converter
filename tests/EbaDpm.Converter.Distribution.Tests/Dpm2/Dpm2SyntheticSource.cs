using System.Data.OleDb;

namespace EbaDpm.Converter.Distribution.Tests.Dpm2;

/// <summary>
/// A tiny synthetic DPM 2.0 <c>.accdb</c> (ADOX/ACE, no EBA data file) with the tables that the
/// release catalog and the "dictionary as of the cutoff release" reads of <c>Dpm20AccessReader</c>
/// query. Only the columns those reads touch exist. Used by the tests marked <c>[AceFact]</c>.
/// </summary>
internal static class Dpm2SyntheticSource
{
    private static readonly string[] Ddl =
    [
        "CREATE TABLE [Release] ([ReleaseID] INTEGER, [Code] TEXT(50), [Date] TEXT(50), [Status] TEXT(50), [Description] TEXT(50), [IsCurrent] YESNO)",
        "CREATE TABLE [Module] ([ModuleID] INTEGER, [FrameworkID] INTEGER, [isDocumentModule] YESNO)",
        "CREATE TABLE [ModuleVersion] ([ModuleVID] INTEGER, [ModuleID] INTEGER, [Code] TEXT(50), [Name] TEXT(100), [StartReleaseID] INTEGER, [EndReleaseID] INTEGER, [VersionNumber] TEXT(20), [FromReferenceDate] TEXT(20))",
        "CREATE TABLE [Category] ([CategoryID] INTEGER, [Code] TEXT(50), [Name] TEXT(100), [Description] TEXT(100), [IsEnumerated] YESNO, [CreatedRelease] INTEGER)",
        "CREATE TABLE [SuperCategoryComposition] ([SuperCategoryID] INTEGER, [CategoryID] INTEGER, [StartReleaseID] INTEGER)",
        "CREATE TABLE [ItemCategory] ([ItemID] INTEGER, [CategoryID] INTEGER, [Code] TEXT(50), [IsDefaultItem] YESNO, [StartReleaseID] INTEGER, [EndReleaseID] INTEGER)",
        "CREATE TABLE [SubCategory] ([SubCategoryID] INTEGER, [CategoryID] INTEGER, [Code] TEXT(50), [Name] TEXT(100))",
        "CREATE TABLE [SubCategoryVersion] ([SubCategoryVID] INTEGER, [SubCategoryID] INTEGER, [StartReleaseID] INTEGER, [EndReleaseID] INTEGER)",
        "CREATE TABLE [PropertyCategory] ([PropertyID] INTEGER, [CategoryID] INTEGER, [StartReleaseID] INTEGER, [EndReleaseID] INTEGER)",
        "CREATE TABLE [Variable] ([VariableID] INTEGER, [Type] TEXT(20))",
        "CREATE TABLE [VariableVersion] ([VariableVID] INTEGER, [VariableID] INTEGER, [PropertyID] INTEGER, [StartReleaseID] INTEGER, [ContextID] INTEGER)",
        "CREATE TABLE [Header] ([HeaderID] INTEGER, [IsKey] YESNO)",
        "CREATE TABLE [HeaderVersion] ([HeaderVID] INTEGER, [HeaderID] INTEGER, [PropertyID] INTEGER, [SubCategoryVID] INTEGER, [StartReleaseID] INTEGER, [ContextID] INTEGER)",
        "CREATE TABLE [TableVersion] ([TableVID] INTEGER, [StartReleaseID] INTEGER, [EndReleaseID] INTEGER, [ContextID] INTEGER)",
        "CREATE TABLE [CompoundItemContext] ([ContextID] INTEGER, [StartReleaseID] INTEGER)",
        "CREATE TABLE [ContextComposition] ([ContextID] INTEGER, [PropertyID] INTEGER)",
    ];

    /// <summary>Runs one statement.</summary>
    public static void Exec(OleDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates the file with every table above (empty), then calls <paramref name="populate"/> on an
    /// open connection. The caller deletes the file (see <see cref="Delete"/>).
    /// </summary>
    public static string Create(Action<OleDbConnection> populate)
    {
        var path = Dpm2TestAccdbFactory.CreateEmptyAccdb();
        using (var connection = new OleDbConnection($"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={path};"))
        {
            connection.Open();
            foreach (var ddl in Ddl)
            {
                Exec(connection, ddl);
            }

            populate(connection);
        }

        OleDbConnection.ReleaseObjectPool();
        return path;
    }

    /// <summary>Best-effort removal of a temp <c>.accdb</c>.</summary>
    public static void Delete(string path)
    {
        OleDbConnection.ReleaseObjectPool();
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp file: a lock must not mask the test verdict.
        }
    }
}
