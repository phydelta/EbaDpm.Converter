using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Core.Validation.Checks;
using Microsoft.Data.Sqlite;
using EbaDpm.Converter.Tests.All;
using EbaDpm.Converter.Tests.Support;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// "One dimension, one axis": the two critical plane A checks <c>A-OCA-10</c> and
/// <c>A-CEL-10</c>, scoped to <see cref="ValidationSourceModel.Dpm2"/>.
///
/// The PROPERTY is asserted ("zero pairs on more than one axis", "zero cells with two members"),
/// NEVER the figure of a broken state: the 2,384 and 6,539 seen in a broken state are evidence
/// that the check KNOWS how to fail, not a number to chase - so they live here as a synthetic
/// POSITIVE CONTROL, not as an assertion on the real output.
///
/// Two blocks:
/// <list type="bullet">
/// <item><description>
/// Synthetic (white box): MINIMAL databases, calling <see cref="CellChecks.Run"/> directly - they
/// prove that the check distinguishes the correct case from the incorrect one, in both directions.
/// </description></item>
/// <item><description>
/// Over the three real outputs (DPM 1.0 4.1, DPM 2.0 4.2, DPM 2.0 4.3), reusing the existing
/// fixtures - no new reconversion. It confirms that the two checks appear and pass in both DPM 2.0
/// outputs, and that they do NOT appear in DPM 1.0 (guarded by <c>model == Dpm2</c> in the
/// production code itself - a scope decision, not something this test should force).
/// </description></item>
/// </list>
/// </summary>
public sealed class AxisOwnershipSyntheticTests : IDisposable
{
    private readonly string _dir;

    public AxisOwnershipSyntheticTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"EbaDpm.AxisOwnershipSynthetic_{Environment.ProcessId}_{Guid.NewGuid():N}");
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
        // MINIMAL fixtures on purpose, like InvariantChecksSyntheticTests: no active FKs
        // (same as the real output while loading, SchemaCreator.Create).
        SqlHelpers.Execute(connection, "PRAGMA foreign_keys = OFF");
        return connection;
    }

    private static void Exec(SqliteConnection c, string sql) => SqlHelpers.Execute(c, sql);

    // -----------------------------------------------------------------------------------------
    // A-OCA-10: no (TableID, DimensionID) touches ordinates of more than one axis.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// CORRECT form: a table with two axes (X and Y), each categorising a DIFFERENT dimension -
    /// no (TableID, DimensionID) steps on two orientations. The check must pass.
    /// </summary>
    [Fact]
    public void AOca10_EachDimensionOnASingleAxis_Passes()
    {
        using var c = CreateAndOpen("aoca10-ok.db");

        Exec(c, "INSERT INTO mAxis (AxisID, AxisOrientation) VALUES (10, 'X'), (20, 'Y')");
        Exec(c, "INSERT INTO mTableAxis (AxisID, TableID, [Order]) VALUES (10, 1, 1), (20, 1, 2)");
        Exec(c, "INSERT INTO mAxisOrdinate (AxisID, OrdinateID) VALUES (10, 100), (20, 200)");
        // Dimension 1 only on the X axis (ordinate 100); dimension 2 only on the Y axis (ordinate 200).
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (100, 1, 1), (200, 2, 2)");

        var results = CellChecks.Run(c, ValidationSourceModel.Dpm2).ToList();
        var aoca10 = Assert.Single(results, r => r.Id == "A-OCA-10");

        Assert.Equal(CheckStatus.Pass, aoca10.Status);
        Assert.Equal(0, aoca10.Failed);
        Assert.True(aoca10.Examined > 0, "positive control: A-OCA-10 did not examine any (TableID,DimensionID) pair.");
    }

    /// <summary>
    /// Positive control: ONE dimension (DimensionID=1) categorises ordinates of TWO different axes
    /// (X and Y) of the SAME table - exactly the real-world phenomenon, reduced to the minimal
    /// case. The check MUST fail.
    /// </summary>
    [Fact]
    public void AOca10_OneDimensionOnTwoAxes_Fails()
    {
        using var c = CreateAndOpen("aoca10-bug.db");

        Exec(c, "INSERT INTO mAxis (AxisID, AxisOrientation) VALUES (10, 'X'), (20, 'Y')");
        Exec(c, "INSERT INTO mTableAxis (AxisID, TableID, [Order]) VALUES (10, 1, 1), (20, 1, 2)");
        Exec(c, "INSERT INTO mAxisOrdinate (AxisID, OrdinateID) VALUES (10, 100), (20, 200)");
        // The SAME dimension (1) categorises an ordinate of the X axis and another of the Y axis.
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (100, 1, 1), (200, 1, 2)");

        var results = CellChecks.Run(c, ValidationSourceModel.Dpm2).ToList();
        var aoca10 = Assert.Single(results, r => r.Id == "A-OCA-10");

        Assert.Equal(CheckStatus.Fail, aoca10.Status);
        Assert.Equal(1, aoca10.Failed);
    }

    /// <summary>
    /// A dimension that appears twice on the SAME axis (two different ordinates of X) is not the
    /// defect A-OCA-10 looks for - it is still ONE axis. It distinguishes "same dimension, same
    /// orientation, two ordinates" (correct, very common: each level of a hierarchy brings its own
    /// row) from "same dimension, two orientations" (the defect).
    /// </summary>
    [Fact]
    public void AOca10_SameDimensionTwoOrdinatesOfTheSameAxis_Passes()
    {
        using var c = CreateAndOpen("aoca10-same-axis.db");

        Exec(c, "INSERT INTO mAxis (AxisID, AxisOrientation) VALUES (10, 'X')");
        Exec(c, "INSERT INTO mTableAxis (AxisID, TableID, [Order]) VALUES (10, 1, 1)");
        Exec(c, "INSERT INTO mAxisOrdinate (AxisID, OrdinateID) VALUES (10, 100), (10, 101)");
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (100, 1, 1), (101, 1, 2)");

        var results = CellChecks.Run(c, ValidationSourceModel.Dpm2).ToList();
        var aoca10 = Assert.Single(results, r => r.Id == "A-OCA-10");

        Assert.Equal(CheckStatus.Pass, aoca10.Status);
        Assert.Equal(0, aoca10.Failed);
    }

    // -----------------------------------------------------------------------------------------
    // A-CEL-10: no NON-shaded cell receives two different members of the same dimension from its
    // own ordinates (recomposed through mCellPosition).
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// CORRECT form: a NON-shaded cell with two positions (one on X, one on Y), each categorising a
    /// DIFFERENT dimension - no dimension arrives twice with different members. The check must pass.
    /// </summary>
    [Fact]
    public void ACel10_EachDimensionASingleMemberInTheCell_Passes()
    {
        using var c = CreateAndOpen("acel10-ok.db");

        Exec(c, "INSERT INTO mTableCell (CellID, TableID, IsShaded) VALUES (1, 1, 0)");
        Exec(c, "INSERT INTO mCellPosition (CellID, OrdinateID) VALUES (1, 100), (1, 200)");
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (100, 1, 1), (200, 2, 2)");

        var results = CellChecks.Run(c, ValidationSourceModel.Dpm2).ToList();
        var acel10 = Assert.Single(results, r => r.Id == "A-CEL-10");

        Assert.Equal(CheckStatus.Pass, acel10.Status);
        Assert.Equal(0, acel10.Failed);
        Assert.True(acel10.Examined > 0, "positive control: A-CEL-10 did not examine any NON-shaded cell.");
    }

    /// <summary>
    /// Positive control: a NON-shaded cell whose two own positions categorise the SAME dimension
    /// with DIFFERENT MEMBERS. It must fail. (A reference copy with ONE contradictory row in
    /// <c>mOrdinateCategorisation</c> takes the cell measure from 0 to a positive count; this is
    /// the minimal case.)
    /// </summary>
    [Fact]
    public void ACel10_NonShadedCellWithTwoMembersOfTheSameDimension_Fails()
    {
        using var c = CreateAndOpen("acel10-bug.db");

        Exec(c, "INSERT INTO mTableCell (CellID, TableID, IsShaded) VALUES (1, 1, 0)");
        Exec(c, "INSERT INTO mCellPosition (CellID, OrdinateID) VALUES (1, 100), (1, 200)");
        // Same dimension (1), DIFFERENT members (1 and 2), in the two own ordinates of the cell.
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (100, 1, 1), (200, 1, 2)");

        var results = CellChecks.Run(c, ValidationSourceModel.Dpm2).ToList();
        var acel10 = Assert.Single(results, r => r.Id == "A-CEL-10");

        Assert.Equal(CheckStatus.Fail, acel10.Status);
        Assert.Equal(1, acel10.Failed);
    }

    /// <summary>
    /// The same contradiction, but on a SHADED cell (<c>IsShaded=1</c>): the statement of A-CEL-10
    /// is EXPLICITLY scoped to "no NON-shaded cell" - a shaded cell with the same anomaly must NOT
    /// count as a failure. It distinguishes "the check ignores shaded cells on purpose" (correct)
    /// from "the check looks at nothing" (a false green) - hence this test explicitly requires
    /// <c>Failed == 0</c> with the SAME data contradiction that the previous test makes fail when
    /// the cell is not shaded.
    /// </summary>
    [Fact]
    public void ACel10_SameContradictionOnAShadedCell_DoesNotCount()
    {
        using var c = CreateAndOpen("acel10-shaded.db");

        Exec(c, "INSERT INTO mTableCell (CellID, TableID, IsShaded) VALUES (1, 1, 1)");
        Exec(c, "INSERT INTO mCellPosition (CellID, OrdinateID) VALUES (1, 100), (1, 200)");
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (100, 1, 1), (200, 1, 2)");

        var results = CellChecks.Run(c, ValidationSourceModel.Dpm2).ToList();
        var acel10 = Assert.Single(results, r => r.Id == "A-CEL-10");

        Assert.Equal(CheckStatus.Pass, acel10.Status);
        Assert.Equal(0, acel10.Failed);
        // And the examined universe (mTableCell WHERE IsShaded=0) is 0: the only cell in this
        // database is shaded, so "pass" here means "there is nothing NON-shaded to look at" -
        // correct by construction of the test, not a false green (the positive control lives in
        // the sibling test, with the SAME contradiction on an IsShaded=0 cell).
        Assert.Equal(0, acel10.Examined);
    }

    // -----------------------------------------------------------------------------------------
    // Scope: the two checks do NOT exist in DPM 1.0 - gated by
    // "model == ValidationSourceModel.Dpm2" in CellChecks.cs itself. They are not enabled here:
    // the properties were measured to hold ALSO in DPM 1.0 and were still left disabled - it is a
    // scope decision, not something this test should force.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void BothChecks_DoNotExistOnDpm1_EvenThoughTheSameDataWouldMakeThemFail()
    {
        using var c = CreateAndOpen("dpm1-gate.db");

        // The same data that makes A-OCA-10/A-CEL-10 fail in DPM 2.0 - the absence of both checks
        // in the DPM 1.0 report is not "there is nothing to fail", it is the scope guard acting.
        Exec(c, "INSERT INTO mAxis (AxisID, AxisOrientation) VALUES (10, 'X'), (20, 'Y')");
        Exec(c, "INSERT INTO mTableAxis (AxisID, TableID, [Order]) VALUES (10, 1, 1), (20, 1, 2)");
        Exec(c, "INSERT INTO mAxisOrdinate (AxisID, OrdinateID) VALUES (10, 100), (20, 200)");
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (100, 1, 1), (200, 1, 2)");
        Exec(c, "INSERT INTO mTableCell (CellID, TableID, IsShaded) VALUES (1, 1, 0)");
        Exec(c, "INSERT INTO mCellPosition (CellID, OrdinateID) VALUES (1, 100), (1, 200)");

        var results = CellChecks.Run(c, ValidationSourceModel.Dpm1).ToList();

        Assert.DoesNotContain(results, r => r.Id == "A-OCA-10");
        Assert.DoesNotContain(results, r => r.Id == "A-CEL-10");
    }
}

// ---------------------------------------------------------------------------------------------
// Over the three real outputs - reuses the existing fixtures, without reconverting. Property
// asserted, never a figure: "0 failures, and the check examined something" in both DPM 2.0
// outputs; "does not appear" in DPM 1.0.
// ---------------------------------------------------------------------------------------------

// Reuses AllFixture.ValidatedDatabasePath - the same --all conversion as the rest of the "All"
// collection, with no open connection on top of it (see AllFixture).
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class AxisOwnershipDpm10Tests(AllFixture fixture)
{
    /// <summary>Guarded to DPM 2.0 in the code itself - they must not appear over DPM 1.0.</summary>
    [DataFact]
    public void Dpm10_All_TheTwoNewChecksDoNotAppear()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null);
        Assert.Equal(0, result.ExitCode);

        Assert.DoesNotContain(result.Report.Checks, c => c.Id is "A-OCA-10" or "A-CEL-10");
    }
}

// Reuses Dpm20SkeletonFixture.ValidatedDatabasePath (collection "Dpm2Skeleton"), without
// reconverting - see the class comment of Dpm20SkeletonFixture.
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class AxisOwnershipDpm2042Tests(Dpm20SkeletonFixture fixture)
{
    /// <summary>
    /// Measured over --all 4.2 (32 taxonomies, 846 tables): 0 of 8,382 (table,dimension) pairs on
    /// more than one axis; 0 of 97,678 non-shaded cells with two members of the same dimension.
    /// Here the PROPERTY is asserted - it passes, and it examined something (positive control) -
    /// not the figures 8,382/97,678, which change with every --all.
    /// </summary>
    [DataFact]
    public void Dpm20Release42_All_AOca10AndACel10Pass()
    {
        Assert.Equal(0, fixture.ExitCode);
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null);
        Assert.Equal(0, result.ExitCode);

        var aoca10 = Assert.Single(result.Report.Checks, c => c.Id == "A-OCA-10");
        Assert.Equal("critical", aoca10.Layer);
        Assert.Equal("pass", aoca10.Status);
        Assert.Equal(0, aoca10.Failed);
        Assert.True(aoca10.Examined > 0, "DPM 2.0 4.2: A-OCA-10 examined 0 pairs (positive control).");

        var acel10 = Assert.Single(result.Report.Checks, c => c.Id == "A-CEL-10");
        Assert.Equal("critical", acel10.Layer);
        Assert.Equal("pass", acel10.Status);
        Assert.Equal(0, acel10.Failed);
        Assert.True(acel10.Examined > 0, "DPM 2.0 4.2: A-CEL-10 examined 0 cells (positive control).");
    }
}

[Collection("Dpm2043ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class AxisOwnershipDpm2043Tests(Dpm2043ValidateFixture fixture)
{
    [DataFact]
    public void Dpm20Release43_All_AOca10AndACel10Pass()
    {
        Assert.Equal(0, fixture.ExitCode);
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, referencePath: null);
        Assert.Equal(0, result.ExitCode);

        var aoca10 = Assert.Single(result.Report.Checks, c => c.Id == "A-OCA-10");
        Assert.Equal("critical", aoca10.Layer);
        Assert.Equal("pass", aoca10.Status);
        Assert.Equal(0, aoca10.Failed);
        Assert.True(aoca10.Examined > 0, "DPM 2.0 4.3: A-OCA-10 examined 0 pairs (positive control).");

        var acel10 = Assert.Single(result.Report.Checks, c => c.Id == "A-CEL-10");
        Assert.Equal("critical", acel10.Layer);
        Assert.Equal("pass", acel10.Status);
        Assert.Equal(0, acel10.Failed);
        Assert.True(acel10.Examined > 0, "DPM 2.0 4.3: A-CEL-10 examined 0 cells (positive control).");
    }
}
