using System.Data.OleDb;
using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Tests.Infrastructure;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// <see cref="Dpm20AccessReader.ReadMetricSubCategoryCandidates"/> over a tiny synthetic Access
/// database (<see cref="AceFactAttribute"/>: no EBA data file, only the Access Database Engine).
/// Rule under test: docs/mapping-dpm2.md, section "mMetric" (issue 61): only NON-key headers, the
/// stable <c>SubCategoryID</c> through <c>SubCategoryVersion</c>, <c>IsCurrent</c> = the header
/// version belongs to a table version current at the cutoff, history = any table version.
/// </summary>
[Trait("Tier", "Synthetic")]
public sealed class Dpm20MetricSubCategoryCandidatesReaderTests
{
    private static readonly string[] Ddl =
    [
        "CREATE TABLE [Release] ([ReleaseID] INTEGER, [Code] TEXT(50), [Date] TEXT(50), [Status] TEXT(50), [Description] TEXT(50), [IsCurrent] YESNO)",
        "CREATE TABLE [TableVersion] ([TableVID] INTEGER, [StartReleaseID] INTEGER, [EndReleaseID] INTEGER)",
        "CREATE TABLE [TableVersionHeader] ([TableVID] INTEGER, [HeaderVID] INTEGER)",
        "CREATE TABLE [Header] ([HeaderID] INTEGER, [IsKey] YESNO)",
        "CREATE TABLE [HeaderVersion] ([HeaderVID] INTEGER, [HeaderID] INTEGER, [PropertyID] INTEGER, [SubCategoryVID] INTEGER)",
        "CREATE TABLE [SubCategoryVersion] ([SubCategoryVID] INTEGER, [SubCategoryID] INTEGER, [StartReleaseID] INTEGER, [EndReleaseID] INTEGER)",
    ];

    private static void Exec(OleDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<Dpm20MetricSubCategoryCandidateRow> Read(Action<OleDbConnection> populate, string cutoffCode = "2.0")
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

                Exec(oledb, "INSERT INTO [Release] ([ReleaseID],[Code],[IsCurrent]) VALUES (1,'1.0',0)");
                Exec(oledb, "INSERT INTO [Release] ([ReleaseID],[Code],[IsCurrent]) VALUES (2,'2.0',-1)");
                populate(oledb);
            }

            OleDbConnection.ReleaseObjectPool();

            using var reader = new Dpm20AccessReader(accdb, cutoffReleaseCode: cutoffCode);
            reader.Open();
            return reader.ReadMetricSubCategoryCandidates().ToList();
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

    private static void Populate(OleDbConnection c)
    {
        // Table versions: TV1 = [1,2) is past at cutoff 2.0, TV2 = [2,-) is current.
        Exec(c, "INSERT INTO [TableVersion] VALUES (1,1,2)");
        Exec(c, "INSERT INTO [TableVersion] VALUES (2,2,NULL)");

        // Header 1 = non-key, header 2 = key.
        Exec(c, "INSERT INTO [Header] VALUES (1,0)");
        Exec(c, "INSERT INTO [Header] VALUES (2,-1)");

        // Sub-category versions: 1000 and 1001 are two versions of SubCategoryID 500; 1002 is SubCategoryID 501.
        Exec(c, "INSERT INTO [SubCategoryVersion] VALUES (1000,500,1,NULL)");
        Exec(c, "INSERT INTO [SubCategoryVersion] VALUES (1001,500,2,NULL)");
        Exec(c, "INSERT INTO [SubCategoryVersion] VALUES (1002,501,1,NULL)");

        // Header versions (HeaderVID, HeaderID, PropertyID, SubCategoryVID).
        Exec(c, "INSERT INTO [HeaderVersion] VALUES (10,1,100,1000)"); // non-key, current table, property 100
        Exec(c, "INSERT INTO [HeaderVersion] VALUES (11,1,100,1001)"); // non-key, past table, property 100, re-versioned
        Exec(c, "INSERT INTO [HeaderVersion] VALUES (12,2,101,1000)"); // KEY header, current table: must be ignored
        Exec(c, "INSERT INTO [HeaderVersion] VALUES (13,1,102,1002)"); // non-key, past table only: history
        Exec(c, "INSERT INTO [HeaderVersion] VALUES (14,1,103,1001)"); // non-key, current, re-versioned sub-category

        // Which table version carries which header version.
        Exec(c, "INSERT INTO [TableVersionHeader] VALUES (2,10)");
        Exec(c, "INSERT INTO [TableVersionHeader] VALUES (1,11)");
        Exec(c, "INSERT INTO [TableVersionHeader] VALUES (2,12)");
        Exec(c, "INSERT INTO [TableVersionHeader] VALUES (1,13)");
        Exec(c, "INSERT INTO [TableVersionHeader] VALUES (2,14)");
    }

    [AceFact]
    public void Candidates_AreNonKeyOnly_StableSubCategoryIds_AndSplitIntoCurrentAndHistory()
    {
        var rows = Read(Populate);

        var expected = new List<Dpm20MetricSubCategoryCandidateRow>
        {
            new(100, 500, false), // header version 10 and 11: any table version
            new(100, 500, true),  // header version 10: the current table version (1000 and 1001 give the same SubCategoryID)
            new(102, 501, false), // only a past table version uses it
            new(103, 500, false),
            new(103, 500, true),  // sub-category version 1001 resolves to the stable SubCategoryID 500
        };
        Assert.Equal(expected.OrderBy(r => r.PropertyId).ThenBy(r => r.IsCurrent), rows.OrderBy(r => r.PropertyId).ThenBy(r => r.IsCurrent));

        // The KEY header (property 101) is in a current table version with a SubCategoryVID: its absence
        // can only come from the key filter (positive control of the exclusion).
        Assert.DoesNotContain(rows, r => r.PropertyId == 101);
    }

    [AceFact]
    public void PastTableVersion_IsHistoryOnly_NeverCurrent()
    {
        var rows = Read(Populate);

        Assert.Equal([new Dpm20MetricSubCategoryCandidateRow(102, 501, false)], rows.Where(r => r.PropertyId == 102));
    }

    [AceFact]
    public void CutoffBeforeTheTableVersionChange_MakesThePastVersionCurrent()
    {
        // With cutoff 1.0, TV1 = [1,2) is current and TV2 = [2,-) is not: the roles swap.
        var rows = Read(Populate, cutoffCode: "1.0");

        Assert.Contains(new Dpm20MetricSubCategoryCandidateRow(102, 501, true), rows);
        Assert.DoesNotContain(new Dpm20MetricSubCategoryCandidateRow(103, 500, true), rows);
        Assert.DoesNotContain(rows, r => r.PropertyId == 101);
    }
}
