using System.Data.OleDb;
using EbaDpm.Converter.Tests.Dpm2;

namespace EbaDpm.Converter.Tests.Infrastructure;

/// <summary>
/// Positive control of <see cref="AccessCensus"/>: on a tiny synthetic <c>.accdb</c> with rows on
/// both sides of every boundary of the "in force" predicate, the census returns the exact counts
/// and changes when a row is added or the release changes. A census that finds the same number
/// whatever the data would make every derived equality in the suite vacuous.
/// </summary>
[Trait("Tier", "Synthetic")]
public sealed class AccessCensusTests
{
    private static void Exec(OleDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [AceFact]
    public void InForce_CountsExactlyTheRowsOfTheReleaseBoundaries_AndSeesOneRowMore()
    {
        var accdb = Dpm2TestAccdbFactory.CreateEmptyAccdb();
        try
        {
            using (var oledb = new OleDbConnection($"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={accdb};"))
            {
                oledb.Open();
                Exec(oledb, "CREATE TABLE [Release] ([ReleaseID] INTEGER, [Code] TEXT(50))");
                Exec(oledb, "CREATE TABLE [Thing] ([StartReleaseID] INTEGER, [EndReleaseID] INTEGER)");
                Exec(oledb, "INSERT INTO [Release] ([ReleaseID],[Code]) VALUES (7,'A')");
                Exec(oledb, "INSERT INTO [Release] ([ReleaseID],[Code]) VALUES (9,'B')");
                // In force at release 7: open-ended from 7 (yes), ends at 7 (no: exclusive), ends at 8 (yes),
                // starts at 8 (no), starts at 6 ending NULL (yes).
                Exec(oledb, "INSERT INTO [Thing] ([StartReleaseID],[EndReleaseID]) VALUES (7,NULL)");
                Exec(oledb, "INSERT INTO [Thing] ([StartReleaseID],[EndReleaseID]) VALUES (5,7)");
                Exec(oledb, "INSERT INTO [Thing] ([StartReleaseID],[EndReleaseID]) VALUES (5,8)");
                Exec(oledb, "INSERT INTO [Thing] ([StartReleaseID],[EndReleaseID]) VALUES (8,NULL)");
                Exec(oledb, "INSERT INTO [Thing] ([StartReleaseID],[EndReleaseID]) VALUES (6,NULL)");
            }

            OleDbConnection.ReleaseObjectPool();

            Assert.Equal(3, AccessCensus.CountInForce(accdb, "Thing", "A"));
            Assert.Equal(5, AccessCensus.Count(accdb, "Thing"));
            Assert.Equal(7, AccessCensus.ReleaseId(accdb, "A"));
            Assert.Throws<InvalidOperationException>(() => AccessCensus.ReleaseId(accdb, "missing"));

            // At release 9 the rows 7/NULL, 8/NULL and 6/NULL are in force, not 5/7 nor 5/8.
            Assert.Equal(3,AccessCensus.CountInForce(accdb, "Thing", "B"));

            using (var oledb = new OleDbConnection($"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={accdb};"))
            {
                oledb.Open();
                Exec(oledb, "INSERT INTO [Thing] ([StartReleaseID],[EndReleaseID]) VALUES (1,NULL)");
            }

            OleDbConnection.ReleaseObjectPool();

            // One more row in force: the census moves by exactly one.
            Assert.Equal(4, AccessCensus.CountInForce(accdb, "Thing", "A"));
            Assert.Equal(6, AccessCensus.Count(accdb, "Thing"));
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
}
