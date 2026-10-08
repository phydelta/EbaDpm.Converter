using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Core.Validation.Checks;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Permanent positive control that <c>I-SIG-09</c>/<c>I-SIG-10</c>, RESTATED after the open-axis
/// sentinel changes, still catch the defect that motivated them - the same one that was checked by
/// hand with an isolated <c>DELETE</c> and <c>UPDATE</c> before accepting the restatement.
///
/// The old statement asserted a PER-ROW equivalence in both directions, and the converse was
/// always FALSE (shown with Access, output and layout agreeing). The new statement keeps the
/// direction that is still true (per row) and moves the converse to the level at which it does
/// hold (per ordinate/axis):
/// <list type="bullet">
/// <item><description><c>I-SIG-09</c>: <c>MemberID = 9999</c> implies an open axis (kept, per row);
/// and every ordinate of an open axis carries AT LEAST one row with <c>MemberID = 9999</c> (new,
/// per ordinate).</description></item>
/// <item><description><c>I-SIG-10</c>: a bracket in the DPS implies a <c>mOpenAxisValueRestriction</c>
/// exists for the axis (kept, per row); and every axis with a restriction carries AT LEAST one row
/// with a bracket (new, per axis).</description></item>
/// </list>
///
/// "If a restated invariant does not fail on the defect it existed to catch, it is wrongly
/// restated." This file is that proof: it builds the CORRECT case (sentinel + fixed pair, exactly
/// the shape the converter produces) and then reproduces the defect by hand - removing the
/// sentinel from an open-axis ordinate (<c>DELETE</c>), deleting the bracket from the only row
/// that carried it on an axis with a restriction (<c>UPDATE</c>) - and confirms that both checks
/// still reject it.
/// </summary>
public sealed class SentinelRestatementSyntheticTests : IDisposable
{
    private readonly string _dir;

    public SentinelRestatementSyntheticTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"EbaDpm.SentinelRestatementSynthetic_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort: a late SQLite handle must not bring down the test suite.
        }
    }

    private SqliteConnection CreateAndOpen(string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        SchemaCreator.Create(path, overwrite: true);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        // MINIMAL fixture on purpose, same pattern as InvariantChecksSyntheticTests/
        // AxisOwnershipSyntheticTests: no active FKs (same as the real output while loading,
        // SchemaCreator.Create).
        SqlHelpers.Execute(connection, "PRAGMA foreign_keys = OFF");
        return connection;
    }

    private static void Exec(SqliteConnection c, string sql) => SqlHelpers.Execute(c, sql);

    private static CheckResult GetSig09(SqliteConnection c) =>
        Assert.Single(ConceptChecks.Run(c, ValidationSourceModel.Dpm1), r => r.Id == "I-SIG-09");

    private static CheckResult GetSig10(SqliteConnection c) =>
        Assert.Single(ConceptChecks.Run(c, ValidationSourceModel.Dpm1), r => r.Id == "I-SIG-10");

    // -----------------------------------------------------------------------------------------
    // I-SIG-09: MemberID=9999 <=> open axis (per row) + an open axis has >= 1 row with 9999 (per
    // ordinate).
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// CORRECT form - exactly the one the converter produces today: an open-axis ordinate carries
    /// the sentinel pair (<c>MemberID=9999</c>) ON ONE DIMENSION and a real FIXED pair on another.
    /// The restated check must pass - if it did not pass on this shape, the fix it is supposed to
    /// validate would have broken it again.
    /// </summary>
    [Fact]
    public void ISig09_OpenAxisOrdinateWithSentinelAndFixedPair_Passes()
    {
        using var c = CreateAndOpen("isig09-ok.db");

        Exec(c, "INSERT INTO mAxis (AxisID, IsOpenAxis) VALUES (1, 1)");
        Exec(c, "INSERT INTO mAxisOrdinate (AxisID, OrdinateID) VALUES (1, 100)");
        // Two dimensions on the SAME ordinate: the sentinel and a fixed pair.
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (100, 1, 9999), (100, 2, 55)");

        var isig09 = GetSig09(c);

        Assert.Equal(CheckStatus.Pass, isig09.Status);
        Assert.Equal(0, isig09.Failed);
        Assert.True(isig09.Examined > 0, "positive control: I-SIG-09 did not examine any row.");
    }

    /// <summary>
    /// The mandatory positive control: reproduces the defect by hand - a <c>DELETE</c> that removes
    /// the sentinel pair from an open-axis ordinate that DID have it, leaving only the fixed pair.
    /// The restated check HAS to keep rejecting it - through the new "per ordinate" direction: the
    /// open-axis ordinate is left without any <c>MemberID=9999</c> row.
    /// </summary>
    [Fact]
    public void ISig09_IfTheSentinelIsRemovedFromAnOpenAxisOrdinate_TheCheckStillFails()
    {
        using var c = CreateAndOpen("isig09-delete.db");

        Exec(c, "INSERT INTO mAxis (AxisID, IsOpenAxis) VALUES (1, 1)");
        Exec(c, "INSERT INTO mAxisOrdinate (AxisID, OrdinateID) VALUES (1, 100)");
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (100, 1, 9999), (100, 2, 55)");

        // The DELETE that reproduces the defect: ONLY the fixed pair remains, without the sentinel.
        Exec(c, "DELETE FROM mOrdinateCategorisation WHERE OrdinateID = 100 AND DimensionID = 1");

        var isig09 = GetSig09(c);

        Assert.Equal(CheckStatus.Fail, isig09.Status);
        Assert.Equal(1, isig09.Failed);
        Assert.True(isig09.Examined > 0, "positive control: I-SIG-09 did not examine any row.");
    }

    /// <summary>
    /// The original "per row" direction is STILL alive: a <c>MemberID=9999</c> on an axis that is
    /// NOT open is still a failure - the restatement did not touch it.
    /// </summary>
    [Fact]
    public void ISig09_IfTheSentinelAppearsOnANonOpenAxis_TheCheckFails()
    {
        using var c = CreateAndOpen("isig09-non-open.db");

        Exec(c, "INSERT INTO mAxis (AxisID, IsOpenAxis) VALUES (1, 0)");
        Exec(c, "INSERT INTO mAxisOrdinate (AxisID, OrdinateID) VALUES (1, 100)");
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (100, 1, 9999)");

        var isig09 = GetSig09(c);

        Assert.Equal(CheckStatus.Fail, isig09.Status);
        Assert.Equal(1, isig09.Failed);
    }

    // -----------------------------------------------------------------------------------------
    // I-SIG-10: bracket in DPS <=> mOpenAxisValueRestriction exists for the axis (per row) + an
    // axis with a restriction has >= 1 row with a bracket (per axis).
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// CORRECT form: an axis with an open-axis restriction carries, in some of its categorisations,
    /// a DPS with a bracket. The restated check must pass.
    /// </summary>
    [Fact]
    public void ISig10_AxisWithRestrictionAndBracket_Passes()
    {
        using var c = CreateAndOpen("isig10-ok.db");

        Exec(c, "INSERT INTO mAxis (AxisID, IsOpenAxis) VALUES (2, 1)");
        Exec(c, "INSERT INTO mAxisOrdinate (AxisID, OrdinateID) VALUES (2, 200)");
        Exec(c, "INSERT INTO mOpenAxisValueRestriction (AxisID, HierarchyID) VALUES (2, 900)");
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID, DPS) VALUES (200, 1, 55, 'eba_dim:DIM(eba_x:x2[*])')");

        var isig10 = GetSig10(c);

        Assert.Equal(CheckStatus.Pass, isig10.Status);
        Assert.Equal(0, isig10.Failed);
        Assert.True(isig10.Examined > 0, "positive control: I-SIG-10 did not examine any row.");
    }

    /// <summary>
    /// The mandatory positive control, <c>UPDATE</c> version: deletes the bracket from the ONLY row
    /// that carried it on an axis that STILL has a restriction. The restated check has to keep
    /// rejecting it - through the new "per axis" direction: the axis with a restriction is left
    /// without any row with a bracket in the DPS.
    /// </summary>
    [Fact]
    public void ISig10_IfTheBracketIsRemovedFromTheOnlyRowOfAnAxisWithRestriction_TheCheckStillFails()
    {
        using var c = CreateAndOpen("isig10-update.db");

        Exec(c, "INSERT INTO mAxis (AxisID, IsOpenAxis) VALUES (2, 1)");
        Exec(c, "INSERT INTO mAxisOrdinate (AxisID, OrdinateID) VALUES (2, 200)");
        Exec(c, "INSERT INTO mOpenAxisValueRestriction (AxisID, HierarchyID) VALUES (2, 900)");
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID, DPS) VALUES (200, 1, 55, 'eba_dim:DIM(eba_x:x2[*])')");

        // The UPDATE that reproduces the defect: the axis STILL has a restriction, but none of its
        // rows carries a bracket in the DPS any more.
        Exec(c, "UPDATE mOrdinateCategorisation SET DPS = 'eba_dim:DIM(eba_x:x2)' WHERE OrdinateID = 200 AND DimensionID = 1");

        var isig10 = GetSig10(c);

        Assert.Equal(CheckStatus.Fail, isig10.Status);
        Assert.Equal(1, isig10.Failed);
        Assert.True(isig10.Examined > 0, "positive control: I-SIG-10 did not examine any row.");
    }

    /// <summary>
    /// The original "per row" direction is STILL alive: a DPS with a bracket on an axis WITHOUT a
    /// restriction is still a failure - the restatement did not touch it.
    /// </summary>
    [Fact]
    public void ISig10_IfTheBracketAppearsOnAnAxisWithoutRestriction_TheCheckFails()
    {
        using var c = CreateAndOpen("isig10-no-restriction.db");

        Exec(c, "INSERT INTO mAxis (AxisID, IsOpenAxis) VALUES (2, 1)");
        Exec(c, "INSERT INTO mAxisOrdinate (AxisID, OrdinateID) VALUES (2, 200)");
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID, DPS) VALUES (200, 1, 55, 'eba_dim:DIM(eba_x:x2[*])')");

        var isig10 = GetSig10(c);

        Assert.Equal(CheckStatus.Fail, isig10.Status);
        Assert.Equal(1, isig10.Failed);
    }
}
