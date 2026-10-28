using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Core.Validation.Checks;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// <c>C-MHR-01</c> (Critical) and <c>C-MHR-02</c> (Informative) on small synthetic outputs and layout
/// repositories (issue 61, docs/validation.md): the hierarchy annotated in the layout
/// (<c>(qAYV) ...[qCO:new_CO5]</c>) must equal the hierarchy of the matching ordinate of the output
/// (<c>mOrdinateMetricRestriction</c>, else <c>mMetric.ReferencedHierarchyID</c>). Pass, violation,
/// Skipped (old repository without the columns, output without the table, nothing compared) and the
/// census census/DD-27 gate. The output is the real empty schema (<see cref="SchemaCreator"/>), the
/// layout repository the real one (<c>LayoutRepositorySchema</c>), so column drift fails here.
/// </summary>
public sealed class PlaneCMetricHierarchySyntheticTests : IDisposable
{
    private readonly string _dir;
    private int _counter;

    public PlaneCMetricHierarchySyntheticTests()
    {
        _dir = Path.Join(Path.GetTempPath(), $"EbaDpm.CMhr_{Environment.ProcessId}_{Guid.NewGuid():N}");
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
            // Best-effort.
        }
    }

    private static void Exec(SqliteConnection c, string sql) => SqlHelpers.Execute(c, sql);

    /// <summary>
    /// Output: table T_01 of release TEST 4.2 with ordinate 0020 on axis X carrying MET qAYV,
    /// metric domain qCO, hierarchies new_CO5 and new_CO4 of qCO, restriction on the ordinate =
    /// <paramref name="restrictionHierarchyId"/> (null: no row), metric hierarchy =
    /// <paramref name="metricHierarchyId"/>.
    /// </summary>
    private SqliteConnection Output(long? restrictionHierarchyId, long? metricHierarchyId, string release = "4.2", bool markCurrent = false)
    {
        var path = Path.Join(_dir, $"out{_counter++}.db");
        SchemaCreator.Create(path, overwrite: true);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        c.Open();
        Exec(c, "PRAGMA foreign_keys = OFF");
        Exec(c, "INSERT INTO mReportingFramework (FrameworkID, FrameworkCode) VALUES (1, 'TEST')");
        Exec(c, $"INSERT INTO mTaxonomy (TaxonomyID, TaxonomyCode, FrameworkID) VALUES (1, 'test {release}', 1)");
        Exec(c, "INSERT INTO mTable (TableID, TableCode) VALUES (1, 'T_01')");
        Exec(c, "INSERT INTO mTaxonomyTable (TaxonomyID, TableID, AnnotatedTableID) VALUES (1, 1, 1)");
        Exec(c, "INSERT INTO mAxis (AxisID, AxisOrientation, IsOpenAxis) VALUES (1, 'X', 0)");
        Exec(c, "INSERT INTO mTableAxis (TableID, AxisID, [Order]) VALUES (1, 1, 1)");
        Exec(c, "INSERT INTO mAxisOrdinate (AxisID, OrdinateID, OrdinateCode) VALUES (1, 100, '0020')");
        Exec(c, "INSERT INTO mDomain (DomainID, DomainCode) VALUES (1, 'qCO'), (2, 'MET'), (3, 'qOTHER')");
        Exec(c, "INSERT INTO mMember (MemberID, DomainID, MemberCode) VALUES (10, 2, 'qAYV')");
        Exec(c, "INSERT INTO mDimension (DimensionID, DimensionCode, DimensionXBRLCode, DomainID) VALUES (9999, 'MET', 'MET', 2)");
        Exec(c, "INSERT INTO mHierarchy (HierarchyID, HierarchyCode, DomainID) VALUES (1, 'new_CO5', 1), (2, 'new_CO4', 1), (3, 'new_CO5', 3)");
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (100, 9999, 10)");
        Exec(c, $"INSERT INTO mMetric (MetricID, CorrespondingMemberID, DataType, ReferencedDomainID, ReferencedHierarchyID) VALUES (1, 10, 'Enumeration/Code', 1, {Sql(metricHierarchyId)})");
        if (restrictionHierarchyId is { } h)
        {
            Exec(c, $"INSERT INTO mOrdinateMetricRestriction (OrdinateID, HierarchyID) VALUES (100, {h})");
        }

        if (markCurrent)
        {
            Exec(c, $"INSERT INTO mRelease (ReleaseID, ReleaseCode, IsCurrent) VALUES (1, '{release}', 1)");
        }

        return c;
    }

    private static string Sql(long? v) => v is null ? "NULL" : v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Layout repository with one metric value of qAYV annotated <c>[hierarchyDomain:hierarchy]</c> on ordinate 0020 of axis X of T_01.</summary>
    private SqliteConnection Layouts(string hierarchyDomain = "qCO", string hierarchy = "new_CO5", string release = "4.2", string ordinateCode = "0020", bool withBracketColumns = true)
    {
        var path = Path.Join(_dir, $"lay{_counter++}.db");
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        c.Open();
        LayoutRepositorySchema.Create(c);
        Exec(c, $"INSERT INTO LayoutFile (FileId, Path, Sha256, FrameworkCode, ModuleCode, ReleaseLabel) VALUES (1, 'p', 's', 'TEST', 'M', '{release}')");
        Exec(c, "INSERT INTO LayoutSheet (SheetId, FileId, SheetName, TableCode) VALUES (1, 1, 'T_01', 'T_01')");
        Exec(c, "INSERT INTO LayoutDeclaration (DeclId, SheetId, Region, CellRef, DomainCode, IsKey) VALUES (1, 1, 'Header', 'D1', 'MET', 0)");
        Exec(c, $"INSERT INTO LayoutOrdinate (OrdinateId, SheetId, Axis, OrdinateCode, IsFallbackCode) VALUES (1, 1, 'X', '{ordinateCode}', 0)");
        Exec(c, $"INSERT INTO LayoutValue (ValueId, SheetId, DeclId, OrdinateId, DomainCode, MemberCode, Label, CellRef, HierarchyDomainCode, HierarchyCode) VALUES (1, 1, 1, 1, 'MET', 'qAYV', 'Type', 'D2', '{hierarchyDomain}', '{hierarchy}')");
        if (!withBracketColumns)
        {
            Exec(c, "ALTER TABLE LayoutValue DROP COLUMN HierarchyDomainCode");
            Exec(c, "ALTER TABLE LayoutValue DROP COLUMN HierarchyCode");
        }

        return c;
    }

    private static (CheckResult C01, CheckResult C02) Run(SqliteConnection generated, SqliteConnection layouts, List<KnownExceptions.Outcome>? sink = null)
    {
        var results = PlaneCChecks.Run(generated, layouts, sink ?? []).ToList();
        return (Assert.Single(results, r => r.Id == "C-MHR-01"), Assert.Single(results, r => r.Id == "C-MHR-02"));
    }

    // ---- Pass ----

    [Fact]
    public void RestrictionOfTheOrdinate_EqualToTheLayout_Passes_AndTheCensusCountsIt()
    {
        using var g = Output(restrictionHierarchyId: 1, metricHierarchyId: null);
        using var l = Layouts();

        var (c01, c02) = Run(g, l);

        Assert.Equal(CheckStatus.Pass, c01.Status);
        Assert.Equal(ValidationLayer.Critical, c01.Layer);
        Assert.Equal(1, c01.Examined);
        Assert.Equal(0, c01.Failed);
        Assert.Equal(CheckStatus.Info, c02.Status);
        Assert.Equal(ValidationLayer.Informative, c02.Layer);
        Assert.Contains("1 total units, 1 matched and compared, 0 matched with no generated hierarchy", c02.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutARestriction_TheMetricHierarchy_IsComparedInstead()
    {
        using var g = Output(restrictionHierarchyId: null, metricHierarchyId: 1);
        using var l = Layouts();

        var (c01, _) = Run(g, l);

        Assert.Equal(CheckStatus.Pass, c01.Status);
        Assert.Equal(1, c01.Examined);
    }

    [Fact]
    public void TheRestrictionWinsOverTheMetricHierarchy()
    {
        // The metric says new_CO4, the ordinate says new_CO5 (the layout too): not a violation.
        using var g = Output(restrictionHierarchyId: 1, metricHierarchyId: 2);
        using var l = Layouts();

        var (c01, _) = Run(g, l);

        Assert.Equal(CheckStatus.Pass, c01.Status);
    }

    // ---- Violations (positive controls) ----

    [Fact]
    public void RestrictionOfAnotherHierarchy_IsAViolation_NamedByBusinessKey()
    {
        using var g = Output(restrictionHierarchyId: 2, metricHierarchyId: null);
        using var l = Layouts(); // layout says new_CO5, output says new_CO4

        var (c01, _) = Run(g, l);

        Assert.Equal(CheckStatus.Fail, c01.Status);
        Assert.Equal(1, c01.Failed);
        var sample = Assert.Single(c01.Samples);
        Assert.Equal("T_01\t4.2|X|0020|qAYV", sample.BusinessKey);
        Assert.Equal("qCO:new_CO4 (from ordinate)", sample.Generated);
        Assert.Equal("qCO:new_CO5", sample.Reference);
    }

    [Fact]
    public void WithoutARestriction_TheMetricHierarchyOfAnotherCode_IsAViolation()
    {
        using var g = Output(restrictionHierarchyId: null, metricHierarchyId: 2);
        using var l = Layouts();

        var (c01, _) = Run(g, l);

        Assert.Equal(CheckStatus.Fail, c01.Status);
        Assert.Equal("qCO:new_CO4 (from metric)", Assert.Single(c01.Samples).Generated);
    }

    [Fact]
    public void SameHierarchyCodeInAnotherDomain_IsAViolation()
    {
        // Hierarchy 3 is also named new_CO5 but belongs to domain qOTHER.
        using var g = Output(restrictionHierarchyId: 3, metricHierarchyId: null);
        using var l = Layouts();

        var (c01, _) = Run(g, l);

        Assert.Equal(CheckStatus.Fail, c01.Status);
        Assert.Equal("qOTHER:new_CO5 (from ordinate)", Assert.Single(c01.Samples).Generated);
    }

    [Fact]
    public void ALayoutDomainWithTheEbaPrefix_IsComparedWithoutIt_AndPasses()
    {
        using var g = Output(restrictionHierarchyId: 1, metricHierarchyId: null);
        using var l = Layouts(hierarchyDomain: "eba_exp:qCO");

        Assert.Equal(CheckStatus.Pass, Run(g, l).C01.Status);
    }

    /// <summary>
    /// The census of known divergences holds no <c>hierarchy</c> (DD-27) entry today, so a real
    /// violation on an output of the census release is never absorbed: it fails (the consulted path,
    /// <c>mRelease.IsCurrent = 1</c> = <c>4.2</c>).
    /// </summary>
    [Fact]
    public void AViolation_OnAnOutputOfTheCensusRelease_IsNotAbsorbedWhenNoDd27EntryNamesIt()
    {
        Assert.Equal("4.2", KnownExceptions.PlaneCCensusRelease);
        Assert.DoesNotContain(KnownExceptions.All, e => e.Id == "DD-27"); // the premise of this test

        using var g = Output(restrictionHierarchyId: 2, metricHierarchyId: null, markCurrent: true);
        using var l = Layouts();
        var sink = new List<KnownExceptions.Outcome>();

        var (c01, _) = Run(g, l, sink);

        Assert.Equal(CheckStatus.Fail, c01.Status);
        Assert.Equal(1, c01.Failed);
        Assert.DoesNotContain(sink, o => o.Exception.Id == "DD-27");
    }

    // ---- Skipped ----

    [Fact]
    public void OldLayoutRepository_WithoutTheBracketColumns_IsSkipped_WithAReason()
    {
        using var g = Output(restrictionHierarchyId: 1, metricHierarchyId: null);
        using var l = Layouts(withBracketColumns: false);

        var (c01, c02) = Run(g, l);

        Assert.Equal(CheckStatus.Skipped, c01.Status);
        Assert.Contains("HierarchyDomainCode/HierarchyCode absent", c01.SkipReason, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Skipped, c02.Status);
    }

    [Fact]
    public void OutputWithoutTheExtensionTable_IsSkipped_WithAReason()
    {
        using var g = Output(restrictionHierarchyId: null, metricHierarchyId: 1);
        Exec(g, "DROP TABLE mOrdinateMetricRestriction");
        using var l = Layouts();

        var (c01, c02) = Run(g, l);

        Assert.Equal(CheckStatus.Skipped, c01.Status);
        Assert.Contains("mOrdinateMetricRestriction", c01.SkipReason, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Skipped, c02.Status);
    }

    [Fact]
    public void NothingToCompare_ExpressedAsSkipped_NotAHollowPass_AndTheCensusExplainsWhy()
    {
        // Matched ordinate, but neither a restriction nor a metric hierarchy: 0 units compared.
        using var g = Output(restrictionHierarchyId: null, metricHierarchyId: null);
        using var l = Layouts();

        var (c01, c02) = Run(g, l);

        Assert.Equal(CheckStatus.Skipped, c01.Status);
        Assert.Contains("0 units compared", c01.SkipReason, StringComparison.Ordinal);
        Assert.Contains("1 matched with no generated hierarchy", c02.Statement, StringComparison.Ordinal);
    }

    // ---- Not matched: reported in C-MHR-02, never compared ----

    [Fact]
    public void AnOrdinateThatTheOutputDoesNotHave_IsNotMatched_AndCounted()
    {
        using var g = Output(restrictionHierarchyId: 1, metricHierarchyId: null);
        using var l = Layouts(ordinateCode: "0999");

        var (c01, c02) = Run(g, l);

        Assert.Equal(CheckStatus.Skipped, c01.Status); // nothing compared
        Assert.Contains("1 not matched (ordinate not found on the axis: 1)", c02.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void ALayoutOfAnotherRelease_IsNotMatched_AndCounted()
    {
        using var g = Output(restrictionHierarchyId: 1, metricHierarchyId: null, release: "4.2");
        using var l = Layouts(release: "4.3");

        var (c01, c02) = Run(g, l);

        Assert.Equal(CheckStatus.Skipped, c01.Status);
        Assert.Contains("1 not matched (different release: 1)", c02.Statement, StringComparison.Ordinal);
    }

    /// <summary>The unit is matched by the metric as well: another metric on the ordinate is not a match.</summary>
    [Fact]
    public void AnOrdinateCarryingAnotherMetric_IsNotMatched()
    {
        using var g = Output(restrictionHierarchyId: 1, metricHierarchyId: null);
        Exec(g, "UPDATE mMember SET MemberCode = 'qZZZ' WHERE MemberID = 10");
        using var l = Layouts();

        var (_, c02) = Run(g, l);

        Assert.Contains("ordinate without that MET categorisation: 1", c02.Statement, StringComparison.Ordinal);
    }

    // ---- DD-27 (type "hierarchy") through an injected census ----

    private const string Key = "T_01\t4.2|X|0020|qAYV";

    private static KnownExceptions.PlaneCCensus Census(string release, params string[] rows)
        => KnownExceptions.ParsePlaneCCensus(
            $"# CorpusRelease: {release}\n" + "TableCode\tType\tLayoutSignature\n" + string.Join("\n", rows) + "\n");

    private static CheckResult RunWithCensus(
        SqliteConnection g, SqliteConnection l, KnownExceptions.PlaneCCensus census, List<KnownExceptions.Outcome> sink)
        => Assert.Single(PlaneCChecks.Run(g, l, sink, census).ToList(), r => r.Id == "C-MHR-01");

    [Fact]
    public void Dd27_EntryNamingAViolatingUnit_AbsorbsIt_AndTheOutcomeIsRecorded()
    {
        using var g = Output(restrictionHierarchyId: 2, metricHierarchyId: null, markCurrent: true); // violates
        using var l = Layouts();
        var sink = new List<KnownExceptions.Outcome>();

        var c01 = RunWithCensus(g, l, Census("4.2", "T_01\thierarchy\t4.2|X|0020|qAYV\tlayout wrong, arbitrated"), sink);

        Assert.Equal(CheckStatus.Pass, c01.Status);
        var outcome = Assert.Single(sink, o => o.Exception.Id == "DD-27");
        Assert.Equal(1, outcome.Matched);
        Assert.False(outcome.Stale);
        Assert.Equal(Key, outcome.Exception.BusinessKey);

        // Positive control: the same output without the entry fails.
        Assert.Equal(CheckStatus.Fail, RunWithCensus(g, l, Census("4.2"), []).Status);
    }

    [Fact]
    public void Dd27_StaleEntry_UnitNoLongerViolating_MakesTheCheckFail()
    {
        using var g = Output(restrictionHierarchyId: 1, metricHierarchyId: null, markCurrent: true); // matches the layout
        using var l = Layouts();
        var sink = new List<KnownExceptions.Outcome>();

        var c01 = RunWithCensus(g, l, Census("4.2", "T_01\thierarchy\t4.2|X|0020|qAYV\tlayout wrong, arbitrated"), sink);

        Assert.Equal(CheckStatus.Fail, c01.Status);
        Assert.Equal(1, c01.Failed);
        Assert.True(Assert.Single(sink, o => o.Exception.Id == "DD-27").Stale);
        Assert.Contains(c01.Samples, x => x.BusinessKey.Contains("DD-27", StringComparison.Ordinal) || x.BusinessKey.Contains("T_01", StringComparison.Ordinal));
    }

    [Fact]
    public void Dd27_EntryOfAnotherCorpusRelease_IsNotConsulted_SoTheViolationFails()
    {
        using var g = Output(restrictionHierarchyId: 2, metricHierarchyId: null, markCurrent: true); // output release 4.2
        using var l = Layouts();
        var sink = new List<KnownExceptions.Outcome>();

        var c01 = RunWithCensus(g, l, Census("4.3", "T_01\thierarchy\t4.2|X|0020|qAYV\treason"), sink);

        Assert.Equal(CheckStatus.Fail, c01.Status);
        Assert.Equal(1, c01.Failed);
        Assert.DoesNotContain(sink, o => o.Exception.Id == "DD-27");
    }

    [Theory]
    [InlineData("T_01\thierarchy\t4.2|X|0020|qAYV")] // no reason column
    [InlineData("T_01\thierarchy\t4.2|X|0020|qAYV\t")] // empty reason
    [InlineData("T_01\thierarchy\t4.2|X|0020|qAYV\treason\textra")] // too many columns
    public void Dd27_MalformedHierarchyRow_ThrowsAtParse(string row)
        => Assert.Throws<InvalidOperationException>(() => Census("4.2", row));
}
