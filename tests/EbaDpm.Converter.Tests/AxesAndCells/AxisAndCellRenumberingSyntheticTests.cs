using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Mapping;
using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Tests.Dictionary;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.AxesAndCells;

/// <summary>
/// Issue 70, synthetic (no data files): <c>AxisAndCellLoader</c> must translate every Access
/// <c>MemberID</c> it writes through <see cref="MemberIdRenumbering"/>. The world is a DPM 1.0
/// source in which a REAL member sits at the Access ID 9999 (the ID the target reserves for the
/// "Open" sentinel member; the Access open sentinel is 999) and is renumbered to
/// <see cref="RenumberedId"/>. That member is used (a) in the categorisation of a closed ordinate,
/// (b) as the starting member of the restriction of an open Z axis, which also puts it in the
/// restricted-pair signature, and (c) as a FIXED pair transferred from the Access sentinel ordinate
/// onto the open axis. Expected values are written by hand from the documented rule, never copied
/// from running the code.
/// </summary>
public sealed class AxisAndCellRenumberingSyntheticTests : IDisposable
{
    private const int RenumberedId = 424242;
    private const int SentinelTarget = 9999;

    private readonly string _dir;

    public AxisAndCellRenumberingSyntheticTests()
    {
        _dir = Path.Join(Path.GetTempPath(), $"EbaDpm.AxisRenumber_{Environment.ProcessId}_{Guid.NewGuid():N}");
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

    private static InMemoryDpm1AxisAndCellSource BuildSource(int realMemberAccessId)
    {
        var s = new InMemoryDpm1AxisAndCellSource();
        s.Owners.Add(new AccessOwnerRow(1, "EBA", "ns", "loc", "eba", "c", null, null));

        // Domain 1: metrics (member 1 = the metric). Domain 10: the real members.
        s.Members.Add(new AccessMemberRow(1, 1, "met", "met", false, "eba_met", null, null));
        s.Members.Add(new AccessMemberRow(realMemberAccessId, 10, "R9999", "real member", false, "eba_R9999", null, null));
        s.Members.Add(new AccessMemberRow(5, 10, "R5", "other member", false, "eba_R5", null, null));
        s.Metrics.Add(new AccessMetricRow(1, 1, 1, null, null, null, null, null, null));

        // Dimension 1 = metric dimension; 11 closed (D1), 12 open (D2), 13 fixed pair on the open axis (D3).
        s.Dimensions.Add(new AccessDimensionRow(1, 1, "MET", "Metric", null, false, "MET", null));
        s.Dimensions.Add(new AccessDimensionRow(11, 10, "D1", "D1", null, false, "eba_D1", null));
        s.Dimensions.Add(new AccessDimensionRow(12, 10, "D2", "D2", null, false, "eba_D2", null));
        s.Dimensions.Add(new AccessDimensionRow(13, 10, "D3", "D3", null, false, "eba_D3", null));
        s.Hierarchies.Add(new AccessHierarchyRow(7, "H7", "h", 10, null, null));

        // Restriction 1: hierarchy 7, starting member = the real member, included.
        s.OpenMemberRestrictions.Add(new AccessOpenMemberRestrictionRow(1, 7, realMemberAccessId, true, false, false));

        // Table 1: X closed (x1, x2), Z open (one sentinel ordinate).
        s.Axes.Add(new AccessAxisRow(1, 1, "X", "Columns", null, false, null));
        s.Axes.Add(new AccessAxisRow(2, 1, "Z", "Sheets", null, true, null));
        s.Ordinates.Add(Ordinate(101, 1, "0010", 1));
        s.Ordinates.Add(Ordinate(102, 1, "0020", 2));
        s.Ordinates.Add(Ordinate(201, 2, "999", 1));

        // Closed ordinates: the metric, and D1 = the real member (x1) / D1 = another member (x2).
        s.Categorisations.Add(new AccessOrdinateCategorisationRow(101, 1, 1, null));
        s.Categorisations.Add(new AccessOrdinateCategorisationRow(101, 11, realMemberAccessId, null));
        s.Categorisations.Add(new AccessOrdinateCategorisationRow(102, 1, 1, null));
        s.Categorisations.Add(new AccessOrdinateCategorisationRow(102, 11, 5, null));

        // The Access sentinel ordinate: the open pair (MemberID 999, restriction 1) and a fixed pair.
        s.Categorisations.Add(new AccessOrdinateCategorisationRow(201, 12, 999, 1));
        s.Categorisations.Add(new AccessOrdinateCategorisationRow(201, 13, realMemberAccessId, null));

        s.Cells.Add(new AccessTableCellRow(1, 1, false, "{r0010,c0010}"));
        s.CellPositions.Add(new AccessCellPositionRow(1, 101));
        s.CellPositions.Add(new AccessCellPositionRow(1, 201));
        return s;
    }

    private static AccessAxisOrdinateRow Ordinate(int id, int axisId, string code, int order)
        => new(id, axisId, false, code, "label " + code, order, 0, null, null, false, null, false, null, null, null);

    private SqliteConnection NewDestination(string name)
    {
        var path = Path.Join(_dir, name + ".db");
        SchemaCreator.Create(path, overwrite: false);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        c.Open();
        foreach (var sql in new[]
        {
            // As in the real run: the skeleton loader leaves foreign_keys OFF while the loaders write.
            "PRAGMA foreign_keys = OFF",
            "INSERT INTO mTable (TableID, TableCode) VALUES (1, 'T_01')",
            "INSERT INTO mDomain (DomainID, DomainCode) VALUES (1, 'MET'), (10, 'D10'), (9999, '')",
            $"INSERT INTO mMember (MemberID, DomainID, MemberCode, MemberXBRLCode, IsDefaultMember) VALUES (1, 1, 'met', 'eba_met', 0), (5, 10, 'R5', 'eba_R5', 0), ({RenumberedId}, 10, 'R9999', 'eba_R9999', 0), (9999, 9999, '', NULL, 0)",
            "INSERT INTO mDimension (DimensionID, DimensionCode, DimensionXBRLCode, DomainID) VALUES (1, 'MET', 'MET', 1), (11, 'D1', 'eba_D1', 10), (12, 'D2', 'eba_D2', 10), (13, 'D3', 'eba_D3', 10)",
            "INSERT INTO mHierarchy (HierarchyID, HierarchyCode, DomainID) VALUES (7, 'H7', 10)",
        })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        return c;
    }

    private static List<string?[]> Rows(SqliteConnection c, string sql, int columns) => QueryHelpers.Rows(c, sql, columns);

    private SqliteConnection Run(string name, int realMemberAccessId, MemberIdRenumbering renumbering)
    {
        var c = NewDestination(name);
        AxisAndCellLoader.Load(
            BuildSource(realMemberAccessId), c, new Dictionary<int, int> { [1] = 1 }, renumbering);
        return c;
    }

    // Business-key view of mOrdinateCategorisation: ordinate code, dimension code, member id.
    private const string CategorisationsSql = """
        SELECT CASE a.IsOpenAxis WHEN 1 THEN 'open' ELSE 'closed' END || ':' || o.OrdinateCode,
               d.DimensionCode, oc.MemberID, oc.DimensionMemberSignature
        FROM mOrdinateCategorisation oc
        JOIN mAxisOrdinate o ON o.OrdinateID = oc.OrdinateID
        JOIN mAxis a ON a.AxisID = o.AxisID
        JOIN mDimension d ON d.DimensionID = oc.DimensionID
        ORDER BY 1, d.DimensionCode
        """;

    [Fact]
    public void RealMemberAtAccess9999_IsRenumbered_InEveryColumnTheLoaderWrites()
    {
        using var c = Run("renumbered", 9999, MemberIdRenumbering.ForOccupant(9999, RenumberedId));
        var rows = Rows(c, CategorisationsSql, 4);
        var byKey = rows.ToDictionary(r => (r[0]!, r[1]!), r => (MemberId: long.Parse(r[2]!), Signature: r[3]));

        // (a) closed ordinate: the real member, renumbered; the other member untouched.
        Assert.Equal(RenumberedId, byKey[("closed:0010", "D1")].MemberId);
        Assert.Equal(5, byKey[("closed:0020", "D1")].MemberId);

        // (c) fixed pair transferred onto the open axis: the real member, renumbered.
        var open = byKey.Where(kv => kv.Key.Item1.StartsWith("open:", StringComparison.Ordinal)).ToList();
        Assert.Equal(RenumberedId, Assert.Single(open, kv => kv.Key.Item2 == "D3").Value.MemberId);

        // The open pair keeps the target sentinel, and its signature carries the TRANSLATED starting
        // member inside the restricted-pair signature [hierarchyId;startMemberId;flag].
        var openPair = Assert.Single(open, kv => kv.Key.Item2 == "D2").Value;
        Assert.Equal(SentinelTarget, openPair.MemberId);
        Assert.Equal($"eba_D2(*[7;{RenumberedId};1])", openPair.Signature);

        // (b) the open axis restriction: starting member translated.
        var restriction = Assert.Single(Rows(c, "SELECT HierarchyID, HierarchyStartingMemberID, IsStartingMemberIncluded FROM mOpenAxisValueRestriction", 3));
        Assert.Equal("7", restriction[0]);
        Assert.Equal(RenumberedId.ToString(), restriction[1]);
        Assert.Equal("1", restriction[2]);

        // Nothing but an open-axis categorisation may point at the sentinel, and no restriction at all.
        Assert.Equal(0, QueryHelpers.Scalar(c, "SELECT COUNT(1) FROM mOrdinateCategorisation WHERE MemberID = 9999 AND OrdinateID IN (SELECT o.OrdinateID FROM mAxisOrdinate o JOIN mAxis a ON a.AxisID = o.AxisID WHERE a.IsOpenAxis = 0)"));
        Assert.Equal(0, QueryHelpers.Scalar(c, "SELECT COUNT(1) FROM mOpenAxisValueRestriction WHERE HierarchyStartingMemberID = 9999"));
    }

    [Fact]
    public void CellSignature_ReflectsTheRenumberedMembers()
    {
        using var c = Run("cellsig", 9999, MemberIdRenumbering.ForOccupant(9999, RenumberedId));
        var cell = Assert.Single(Rows(c, "SELECT CAST(CellID AS TEXT), DatapointSignature, DPS FROM mTableCell", 3));
        Assert.NotNull(cell[0]);

        // The D2 open pair is restricted: the ID variant carries hierarchy id, TRANSLATED starting
        // member id and flag; the code variant (DPS) carries codes.
        Assert.Contains($"eba_D2(*[7;{RenumberedId};1])", cell[1]);
        Assert.DoesNotContain("[7;9999;", cell[1]);
        Assert.Contains("eba_D2(*[H7;R9999;1])", cell[2]);
    }

    [Fact]
    public void WithIdentityRenumbering_AccessIdsAreKept_AndTheOpenPairIsStillTheRestrictedOne()
    {
        // Real member at Access 9999 and the identity renumbering (nothing displaced it). The open
        // pair must still be recognised as the open one (Access 999), not confused with the real
        // member at 9999: a confusion would yield the unrestricted "(*)" form or a wrong member.
        using var c = Run("identity", 9999, MemberIdRenumbering.Identity);

        var rows = Rows(c, CategorisationsSql, 4);
        var closed = Assert.Single(rows, r => r[0] == "closed:0010" && r[1] == "D1");
        Assert.Equal("9999", closed[2]);

        var open = Assert.Single(rows, r => r[1] == "D2");
        Assert.Equal("9999", open[2]);
        Assert.Equal("eba_D2(*[7;9999;1])", open[3]);
    }

    [Fact]
    public void PositiveControl_WithoutTheRenumbering_TheClosedCategorisationStaysOnAccess9999()
    {
        // The assertions of the first test can fail: with the identity renumbering (the pre-fix
        // behaviour for these columns) the closed categorisation lands on 9999, not on the renumbered ID.
        using var c = Run("control", 9999, MemberIdRenumbering.Identity);
        var closed = Assert.Single(Rows(c, CategorisationsSql, 4), r => r[0] == "closed:0010" && r[1] == "D1");
        Assert.NotEqual(RenumberedId.ToString(), closed[2]);
        Assert.Equal("9999", closed[2]);
    }
}
