using System.Data.OleDb;
using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Tests.Dpm2;

namespace EbaDpm.Converter.Tests.Infrastructure;

/// <summary>
/// Positive control of the zero in <c>Dpm20DefaultMemberAbsenceTests</c>: on a tiny synthetic
/// <c>.accdb</c> the SAME <see cref="DefaultMemberPairs"/> function returns exactly 1 declared
/// default pair when one variable context carries a default member, and 0 without it.
/// </summary>
[Trait("Tier", "Synthetic")]
public sealed class DefaultMemberPairsTests
{
    private static readonly string[] Ddl =
    [
        "CREATE TABLE [Release] ([ReleaseID] INTEGER, [Code] TEXT(50), [Date] TEXT(50), [Status] TEXT(50), [Description] TEXT(50), [IsCurrent] YESNO)",
        "CREATE TABLE [TableVersionCell] ([TableVID] INTEGER, [CellID] INTEGER, [IsExcluded] YESNO, [IsVoid] YESNO, [VariableVID] INTEGER)",
        "CREATE TABLE [VariableVersion] ([VariableVID] INTEGER, [ContextID] INTEGER)",
        "CREATE TABLE [ContextComposition] ([ContextID] INTEGER, [PropertyID] INTEGER, [ItemID] INTEGER)",
        "CREATE TABLE [ItemCategory] ([ItemID] INTEGER, [CategoryID] INTEGER, [Code] TEXT(50), [IsDefaultItem] YESNO, [StartReleaseID] INTEGER, [EndReleaseID] INTEGER)",
    ];

    private static void Exec(OleDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static DefaultMemberPairs.Result Run(bool plantDefaultInContext)
    {
        var accdb = Dpm2TestAccdbFactory.CreateEmptyAccdb();
        try
        {
            using (var oledb = new OleDbConnection($"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={accdb};"))
            {
                oledb.Open();
                foreach (var ddl in Ddl)
                {
                    Exec(oledb, ddl);
                }

                Exec(oledb, "INSERT INTO [Release] ([ReleaseID],[Code],[IsCurrent]) VALUES (1,'1.0',-1)");
                // Table 10 -> cell with variable 100 -> context 1000 (member 501, and 502 when planted).
                Exec(oledb, "INSERT INTO [TableVersionCell] ([TableVID],[CellID],[IsExcluded],[IsVoid],[VariableVID]) VALUES (10,1,0,0,100)");
                // An excluded cell whose variable/context is outside the universe: it carries the default member 502.
                Exec(oledb, "INSERT INTO [TableVersionCell] ([TableVID],[CellID],[IsExcluded],[IsVoid],[VariableVID]) VALUES (10,2,-1,0,101)");
                Exec(oledb, "INSERT INTO [VariableVersion] ([VariableVID],[ContextID]) VALUES (100,1000)");
                Exec(oledb, "INSERT INTO [VariableVersion] ([VariableVID],[ContextID]) VALUES (101,1001)");
                Exec(oledb, "INSERT INTO [ContextComposition] ([ContextID],[PropertyID],[ItemID]) VALUES (1000,7,501)");
                Exec(oledb, "INSERT INTO [ContextComposition] ([ContextID],[PropertyID],[ItemID]) VALUES (1001,7,502)");
                if (plantDefaultInContext)
                {
                    Exec(oledb, "INSERT INTO [ContextComposition] ([ContextID],[PropertyID],[ItemID]) VALUES (1000,8,502)");
                }

                // 501 is an ordinary member, 502 the default member of its domain.
                Exec(oledb, "INSERT INTO [ItemCategory] ([ItemID],[CategoryID],[Code],[IsDefaultItem],[StartReleaseID],[EndReleaseID]) VALUES (501,1,'a',0,1,NULL)");
                Exec(oledb, "INSERT INTO [ItemCategory] ([ItemID],[CategoryID],[Code],[IsDefaultItem],[StartReleaseID],[EndReleaseID]) VALUES (502,1,'b',-1,1,NULL)");
            }

            OleDbConnection.ReleaseObjectPool();

            using var reader = new Dpm20AccessReader(accdb, cutoffReleaseCode: "1.0");
            reader.Open();
            return DefaultMemberPairs.Count(reader, [10]);
        }
        finally
        {
            OleDbConnection.ReleaseObjectPool();
            try
            {
                File.Delete(accdb);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp file.
            }
        }
    }

    [AceFact]
    public void ADefaultMemberPlantedInAVariableContext_IsCountedExactlyOnce()
    {
        var result = Run(plantDefaultInContext: true);

        Assert.Equal(1, result.DeclaredDefaultPairs);
        Assert.Equal([502], result.DeclaredDefaultItemIds);
        Assert.Equal(2, result.PairCount);
    }

    [AceFact]
    public void WithoutThePlantedRow_NoDefaultPairIsCounted_ButTheDefaultIsKnown()
    {
        var result = Run(plantDefaultInContext: false);

        Assert.Equal(0, result.DeclaredDefaultPairs);
        Assert.Empty(result.DeclaredDefaultItemIds);
        Assert.Equal(1, result.PairCount);
        Assert.Equal(1, result.DefaultItemIdCount); // the default exists: the zero is a real absence
    }
}
