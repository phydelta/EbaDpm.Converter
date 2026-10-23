using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;
using EbaDpm.Converter.Tests.Support;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Runs <c>--validate</c> FOR REAL (<see cref="Validator.Run"/>, the same function the CLI uses)
/// over a dedicated <c>--all</c> conversion. It is the only place in the suite that runs the
/// COMPLETE check, and therefore also the permanent protection of plane A invariants that only
/// <c>--validate</c> exercises -- among them A-OCA-01 (the merge of the ABSTRACT ordinate with its
/// own cells, which without this test would only be checked by hand).
///
/// It reuses <see cref="Dpm20SkeletonFixture.ValidatedDatabasePath"/> -- the copy of the single
/// <c>--all</c> conversion that the fixture produces with no open connection, as
/// <c>Validator.Run</c> requires -- so no second full conversion is needed.
///
/// Without a reference, over <c>--all</c>, all critical checks pass. With the 4.2 reference,
/// <c>B-OCA-01</c> is run and its universe is pinned (<see cref="ReleasePins.Reference42"/>); the
/// number of failures is recorded, not asserted.
///
/// The number of critical checks is pinned to <see cref="ReleasePins.Dpm20Cutoff42"/> (exact, so a
/// check deleted by accident is caught). Adding a critical check makes the pin fail on purpose:
/// it is a re-measure, not a defect.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class Dpm20ValidateSuiteTests(Dpm20SkeletonFixture fixture)
{
    /// <summary>
    /// <c>--validate</c> WITHOUT a reference over the <c>--all</c> conversion. What it protects is
    /// <b>"no critical check fails"</b> -- not an exact number of checks, which grows every time a
    /// new invariant is registered. The count is a scoped pin (see the class comment): it protects
    /// against a harness that is emptied by accident.
    /// </summary>
    [DataFact]
    public void Validate_WithoutReference_OverAll_AllCriticalChecksPass()
    {
        Assert.Equal(0, fixture.ExitCode);

        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null);

        var failingCritical = result.Report.Checks
            .Where(c => c.Layer == "critical" && c.Status == "fail")
            .ToList();

        Assert.True(
            failingCritical.Count == 0,
            $"{failingCritical.Count} critical checks fail without a reference (--all): "
            + string.Join(" | ", failingCritical.Select(c => $"{c.Id}: {c.Failed}/{c.Examined} -- {c.Statement}")));

        Assert.Equal(0, result.Report.Summary.Critical.Failed);

        // Run WITHOUT a reference (referencePath: null above). A drop would be the harness silently
        // losing coverage; a rise is a new critical check, to be re-measured.
        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Cutoff42, "critical checks run without a reference", 216, result.Report.Summary.Critical.Checks);
        Assert.Equal(0, result.ExitCode);

        // Explicit protection of the merge of the ABSTRACT ordinate with its own cells: without it,
        // A-OCA-01 broke on part of the leaves. If someone removes that merge, this assertion --
        // and the ones above -- are what catch it; there is no other one in the whole suite.
        var aoca01 = Assert.Single(result.Report.Checks, c => c.Id == "A-OCA-01");
        Assert.Equal("pass", aoca01.Status);
        Assert.Equal(0, aoca01.Failed);

        // Derived with an independent SQL over the output: the distinct leaf ordinates (not abstract
        // headers) of the closed axes linked to a table.
        var leaves = ScalarOnOutput(
            "SELECT COUNT(DISTINCT o.OrdinateID) FROM mAxisOrdinate o JOIN mAxis a ON a.AxisID = o.AxisID "
            + "JOIN mTableAxis ta ON ta.AxisID = a.AxisID WHERE a.IsOpenAxis = 0 AND o.IsAbstractHeader = 0");
        Assert.True(
            leaves == aoca01.Examined,
            $"A-OCA-01 leaves examined: expected {leaves} (derived by SQL from the generated mAxisOrdinate/mAxis/mTableAxis), actual {aoca01.Examined}.");
    }

    /// <summary>
    /// <c>--validate</c> against <c>EBA_4.2_Hotfix.db</c>. This test records the measured figure of
    /// B-OCA-01, actually executed, and does NOT assert that it passes: it is the permanent evidence
    /// that the check is still alive and of the order of magnitude of the finding, so that a
    /// regression (or a real fix) is noticed.
    /// </summary>
    [DataFact]
    public void Validate_WithReference42_ReportIncludesBOca01_AndItsCurrentStateIsMeasured()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, RepoPaths.ReferenceDatabasePath);

        var bOca01 = Assert.Single(result.Report.Checks, c => c.Id == "B-OCA-01");

        ReleasePins.AssertPinned(ReleasePins.Reference42, "B-OCA-01 leaf ordinates examined", 18556, bOca01.Examined);

        // No concrete number of failures is asserted (similarity of a table that is not compared by
        // census is not pursued): only that the failures are a subset of what was examined.
        Assert.True(
            bOca01.Failed <= bOca01.Examined,
            $"B-OCA-01 failed {bOca01.Failed} of {bOca01.Examined} examined: failures exceed the universe.");
    }

    /// <summary>
    /// A plane B known exception that applies to the 4.2 reference but matches nothing is stale,
    /// and the validator does not gate its exit code on it: this test is the gate. It reuses the
    /// cached validator run (no extra conversion).
    /// Positive control: DD-25 (CODIS label) must apply and be matched, so an empty or
    /// non-evaluated exception list cannot pass.
    /// </summary>
    [DataFact]
    public void Validate_WithReference42_NoPlaneBKnownExceptionIsStale()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, RepoPaths.ReferenceDatabasePath);

        var applicablePlaneB = result.Report.Exceptions
            .Where(e => e.Plane == "B" && e.AppliesToThisReference)
            .ToList();

        // Positive control: the list is not empty and a known-matched exception is in it.
        var dd25 = Assert.Single(applicablePlaneB, e => e.Id == "DD-25" && e.BusinessKey == "pillar3_4.2|CODIS");
        Assert.True(dd25.Matched >= 1, $"DD-25 (CODIS) expected matched >= 1, actual {dd25.Matched}.");
        Assert.False(dd25.Stale, "DD-25 (CODIS) is reported stale.");

        var stale = applicablePlaneB.Where(e => e.Stale).ToList();
        Assert.True(
            stale.Count == 0,
            $"{stale.Count} stale plane B known exception(s) apply to the 4.2 reference: "
            + string.Join(" | ", stale.Select(e => $"Id={e.Id}, BusinessKey={e.BusinessKey}, Scope={e.Scope}")));

        // An exception that applies to this reference and was evaluated must either match or be
        // stale. Matched == 0 and not stale means the exception was silently dropped (DD-26 was
        // once skipped by the containment filter and went unnoticed).
        var silentlyDropped = applicablePlaneB.Where(e => e.Evaluated && e.Matched == 0 && !e.Stale).ToList();
        Assert.True(
            silentlyDropped.Count == 0,
            $"{silentlyDropped.Count} plane B known exception(s) apply to the 4.2 reference, were evaluated, "
            + "and have Matched == 0 without being stale: "
            + string.Join(" | ", silentlyDropped.Select(e => $"Id={e.Id}, Check={e.Scope}, BusinessKey={e.BusinessKey}")));
    }

    /// <summary>
    /// DD-26: dimension <c>TNS</c> is in the 4.2 reference but deliberately not emitted (issue #10).
    /// Both DD-26 entries (dimension code census and dimension XBRL census) must apply to this
    /// reference, be matched and not stale, and the two critical censuses B-DIC-01.3 and
    /// B-DIC-01.6 must pass: no other dimension may be missing. Positive control: TNS is read
    /// from the reference and from the generated file by business key, so the test cannot pass
    /// vacuously (TNS present in the reference, absent from the output).
    /// </summary>
    [DataFact]
    public void Validate_WithReference42_Dd26UnusedDimensionTns_IsMatchedAndDimensionCensusesPass()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        // Positive control: the divergence is real.
        Assert.Equal(1, CountDimension(RepoPaths.ReferenceDatabasePath, "TNS"));
        Assert.Equal(0, CountDimension(fixture.ValidatedDatabasePath, "TNS"));

        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, RepoPaths.ReferenceDatabasePath);

        // All problems are collected so one run shows the complete evidence.
        var problems = new List<string>();

        foreach (var scope in new[] { "B-DIC-01-DIMENSION-CODE", "B-DIC-01-DIMENSION-XBRL" })
        {
            var dd26 = Assert.Single(result.Report.Exceptions, e => e.Id == "DD-26" && e.Scope == scope);
            Assert.Equal("TNS", dd26.BusinessKey);
            if (!dd26.AppliesToThisReference)
            {
                problems.Add($"DD-26 ({scope}) does not apply to the 4.2 reference.");
            }

            if (dd26.Matched < 1)
            {
                problems.Add($"DD-26 ({scope}) expected matched >= 1, actual {dd26.Matched} (evaluated={dd26.Evaluated}, stale={dd26.Stale}).");
            }

            if (dd26.Stale)
            {
                problems.Add($"DD-26 ({scope}) is reported stale.");
            }
        }

        foreach (var id in new[] { "B-DIC-01.3", "B-DIC-01.6" })
        {
            var check = Assert.Single(result.Report.Checks, c => c.Id == id);
            var samples = string.Join(" | ", check.Samples.Select(s => s.BusinessKey));
            if (check.Status != "pass" || check.Failed != 0)
            {
                problems.Add($"{id} status={check.Status}, failed={check.Failed}, examined={check.Examined}; keys: {samples}");
            }

            if (check.Examined <= 0)
            {
                problems.Add($"{id} examined nothing.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private long ScalarOnOutput(string sql)
    {
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = fixture.ValidatedDatabasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long CountDimension(string path, string dimensionCode)
    {
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM \"mDimension\" WHERE \"DimensionCode\" = $code";
        command.Parameters.AddWithValue("$code", dimensionCode);
        return (long)command.ExecuteScalar()!;
    }
}
