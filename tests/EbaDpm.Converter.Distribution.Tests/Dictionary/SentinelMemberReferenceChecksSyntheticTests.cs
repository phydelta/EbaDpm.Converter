using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Distribution.Validation.Checks;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Distribution.Tests.Dictionary;

/// <summary>
/// Issue 70: plane A checks A-SEN-08.1..08.6 (docs/validation.md). The sentinel member (the only
/// member of the sentinel domain 9999) may be referenced only by its own mMember row and by the
/// mOrdinateCategorisation rows of ordinates on open axes. Each sub-check runs on a small clean
/// database (it must pass AND examine at least one row, so a pass is not vacuous) and on the same
/// database with the sentinel planted in exactly the column the check names (positive control: it
/// must fail with exactly that violation). Synthetic, no data files.
/// </summary>
public sealed class SentinelMemberReferenceChecksSyntheticTests : IDisposable
{
    private const int Real = 20;

    private readonly string _dir;

    public SentinelMemberReferenceChecksSyntheticTests()
    {
        _dir = Path.Join(Path.GetTempPath(), $"EbaDpm.SenRefs_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }

    /// <summary>
    /// A clean world: the sentinel member 9999 (domain 9999), a real member 20 (domain 10), a closed
    /// axis (ordinate 100) categorised by the real member, an open axis (ordinate 200) categorised by
    /// the sentinel, and one row in every table the sub-checks examine, all pointing at the real member.
    /// </summary>
    private SqliteConnection Create(string name)
    {
        var path = Path.Join(_dir, name + ".db");
        SchemaCreator.Create(path, overwrite: true);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite }.ToString());
        c.Open();
        Exec(c, "PRAGMA foreign_keys = OFF");
        Exec(c, "INSERT INTO mDomain (DomainID, DomainCode) VALUES (10, 'D10'), (9999, '')");
        Exec(c, $"INSERT INTO mMember (MemberID, DomainID, MemberCode) VALUES (9999, 9999, ''), ({Real}, 10, 'R20'), (21, 10, 'R21')");
        Exec(c, $"INSERT INTO mDimension (DimensionID, DimensionCode, DimensionXBRLCode, DomainID, DefaultMemberID) VALUES (11, 'D1', 'eba_D1', 10, {Real})");
        Exec(c, "INSERT INTO mHierarchy (HierarchyID, HierarchyCode, DomainID) VALUES (7, 'H7', 10)");
        Exec(c, $"INSERT INTO mHierarchyNode (HierarchyNodeID, HierarchyID, MemberID, ParentMemberID) VALUES (1, 7, {Real}, NULL), (2, 7, 21, {Real})");
        Exec(c, "INSERT INTO mAxis (AxisID, AxisOrientation, IsOpenAxis) VALUES (1, 'X', 0), (2, 'Z', 1)");
        Exec(c, "INSERT INTO mAxisOrdinate (OrdinateID, AxisID, OrdinateCode) VALUES (100, 1, '0010'), (200, 2, '0010')");
        Exec(c, $"INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (100, 11, {Real}), (200, 11, 9999)");
        Exec(c, $"INSERT INTO mOpenAxisValueRestriction (AxisID, HierarchyID, HierarchyStartingMemberID, IsStartingMemberIncluded) VALUES (2, 7, {Real}, 1)");
        Exec(c, $"INSERT INTO mOrdinateMetricRestriction (OrdinateID, HierarchyID, HierarchyStartingMemberID, IsStartingMemberIncluded) VALUES (100, 7, {Real}, 1)");
        Exec(c, $"INSERT INTO mMetric (MetricID, CorrespondingMemberID, HierarchyStartingMemberID) VALUES (1, {Real}, {Real})");
        return c;
    }

    private static void Exec(SqliteConnection c, string sql) => SqlHelpers.Execute(c, sql);

    /// <summary>The check runner is a lazy iterator: take the check as soon as it is yielded.</summary>
    private static CheckResult Sen(SqliteConnection c, string id)
        => DictionaryChecks.Run(c, ValidationSourceModel.Dpm1, []).First(r => r.Id == id);

    public static TheoryData<string> SubChecks => new()
    {
        "A-SEN-08.1", "A-SEN-08.2", "A-SEN-08.3", "A-SEN-08.4", "A-SEN-08.5", "A-SEN-08.6",
    };

    [Theory]
    [MemberData(nameof(SubChecks))]
    public void CleanDatabase_Passes_AndExaminesAtLeastOneRow(string id)
    {
        using var c = Create("clean-" + id);
        var r = Sen(c, id);
        Assert.Equal(CheckStatus.Pass, r.Status);
        Assert.Equal(0, r.Failed);
        Assert.True(r.Examined >= 1, $"{id} examined {r.Examined} rows: a pass over nothing is vacuous.");
        Assert.Equal(ValidationLayer.Critical, r.Layer);
    }

    [Fact]
    public void A08_1_SentinelOnAClosedAxisOrdinate_Fails()
    {
        using var c = Create("s081-closed");
        Exec(c, "UPDATE mOrdinateCategorisation SET MemberID = 9999 WHERE OrdinateID = 100");
        var r = Sen(c, "A-SEN-08.1");
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Equal(1, r.Failed);
    }

    [Fact]
    public void A08_1_SentinelOnAnOrdinateWithNoAxisRow_Fails()
    {
        // An ordinate that belongs to no axis cannot be on an open axis: the sentinel there is a violation.
        using var c = Create("s081-orphan");
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (999999, 11, 9999)");
        var r = Sen(c, "A-SEN-08.1");
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Equal(1, r.Failed);
    }

    [Fact]
    public void A08_1_SentinelOnOpenAxisOrdinates_IsAllowed_EvenOnSeveralRows()
    {
        using var c = Create("s081-open-many");
        Exec(c, "INSERT INTO mDimension (DimensionID, DimensionCode, DimensionXBRLCode, DomainID) VALUES (12, 'D2', 'eba_D2', 10)");
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (200, 12, 9999)");
        var r = Sen(c, "A-SEN-08.1");
        Assert.Equal(CheckStatus.Pass, r.Status);
        Assert.Equal(2, r.Examined);
    }

    [Theory]
    [InlineData("A-SEN-08.2", "UPDATE mOpenAxisValueRestriction SET HierarchyStartingMemberID = 9999")]
    [InlineData("A-SEN-08.3", "UPDATE mOrdinateMetricRestriction SET HierarchyStartingMemberID = 9999")]
    [InlineData("A-SEN-08.4", "UPDATE mHierarchyNode SET MemberID = 9999 WHERE HierarchyNodeID = 1")]
    [InlineData("A-SEN-08.4", "UPDATE mHierarchyNode SET ParentMemberID = 9999 WHERE HierarchyNodeID = 2")]
    [InlineData("A-SEN-08.5", "UPDATE mDimension SET DefaultMemberID = 9999")]
    [InlineData("A-SEN-08.6", "UPDATE mMetric SET CorrespondingMemberID = 9999")]
    [InlineData("A-SEN-08.6", "UPDATE mMetric SET HierarchyStartingMemberID = 9999")]
    public void PlantedSentinelInTheForbiddenColumn_Fails(string id, string plant)
    {
        using var c = Create("plant-" + id + "-" + Math.Abs(plant.GetHashCode()));
        Assert.Equal(CheckStatus.Pass, Sen(c, id).Status); // the same database is clean before the plant
        Exec(c, plant);

        var r = Sen(c, id);
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Equal(1, r.Failed);
    }

    [Fact]
    public void PlantingInOneColumn_FailsOnlyThatSubCheck()
    {
        using var c = Create("isolation");
        Exec(c, "UPDATE mOpenAxisValueRestriction SET HierarchyStartingMemberID = 9999");
        Assert.Equal(CheckStatus.Fail, Sen(c, "A-SEN-08.2").Status);
        foreach (var other in new[] { "A-SEN-08.1", "A-SEN-08.3", "A-SEN-08.4", "A-SEN-08.5", "A-SEN-08.6" })
        {
            Assert.Equal(CheckStatus.Pass, Sen(c, other).Status);
        }
    }

    [Fact]
    public void SentinelIsLookedUpInTheDatabase_NotHardCoded()
    {
        // When there is no member of the sentinel domain, "IN (empty)" matches nothing: no row can
        // violate, so the check passes. A 9999 in a column is then NOT treated as the sentinel by an
        // accidental literal. (Presence of the sentinel member itself is A-SEN-02's job.)
        using var c = Create("no-sentinel");
        Exec(c, "DELETE FROM mMember WHERE DomainID = 9999");
        Exec(c, "UPDATE mDimension SET DefaultMemberID = 9999");
        Assert.Equal(CheckStatus.Pass, Sen(c, "A-SEN-08.5").Status);
    }
}
