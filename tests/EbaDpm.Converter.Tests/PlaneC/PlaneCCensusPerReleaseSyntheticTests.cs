using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Core.Validation.Checks;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// Plane C census per release (#67). Every embedded <c>plane-c-known-divergences-&lt;release&gt;.tsv</c> is
/// loaded; the census applied to an output is the one whose <c># CorpusRelease</c> equals the output's
/// release; two files for one release are an error; with NO census for the release, <c>C-DPS-01</c> and
/// <c>C-MHR-01</c> are EVALUATED with an EMPTY census (an empty census exempts nothing and nothing can
/// expire), which replaces the earlier skip; DPM 1.0 (no single release) consults no census at all.
/// Synthetic outputs on the real schema, no data files.
/// </summary>
[Trait("Tier", "Synthetic")]
public sealed class PlaneCCensusPerReleaseSyntheticTests : IDisposable
{
    private readonly string _dir;
    private int _counter;

    public PlaneCCensusPerReleaseSyntheticTests()
    {
        _dir = Path.Join(Path.GetTempPath(), $"EbaDpm.CensusPerRelease_{Environment.ProcessId}_{Guid.NewGuid():N}");
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

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// An output of release <paramref name="release"/> (the single <c>IsCurrent</c> row, or every release in
    /// <paramref name="currentReleases"/> when given, the DPM 1.0 shape) with ONE table <c>T_01</c> whose only
    /// cell has signature <c>MET(qAA)|DIM(m1)</c>, and the C-MHR-01 ingredients (ordinate 0020 carrying metric
    /// qAYV restricted to hierarchy <paramref name="restrictionHierarchyId"/>).
    /// </summary>
    private SqliteConnection Output(string release, int restrictionHierarchyId = 1, params string[] currentReleases)
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
        Exec(c, "INSERT INTO mTableCell (CellID, TableID, DPS) VALUES (1, 1, 'MET(qAA)|DIM(m1)')");
        Exec(c, "INSERT INTO mAxis (AxisID, AxisOrientation, IsOpenAxis) VALUES (1, 'X', 0)");
        Exec(c, "INSERT INTO mTableAxis (TableID, AxisID, [Order]) VALUES (1, 1, 1)");
        Exec(c, "INSERT INTO mAxisOrdinate (AxisID, OrdinateID, OrdinateCode) VALUES (1, 100, '0020')");
        Exec(c, "INSERT INTO mDomain (DomainID, DomainCode) VALUES (1, 'qCO'), (2, 'MET')");
        Exec(c, "INSERT INTO mMember (MemberID, DomainID, MemberCode) VALUES (10, 2, 'qAYV')");
        Exec(c, "INSERT INTO mDimension (DimensionID, DimensionCode, DimensionXBRLCode, DomainID) VALUES (9999, 'MET', 'MET', 2)");
        Exec(c, "INSERT INTO mHierarchy (HierarchyID, HierarchyCode, DomainID) VALUES (1, 'new_CO5', 1), (2, 'new_CO4', 1)");
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (100, 9999, 10)");
        Exec(c, "INSERT INTO mMetric (MetricID, CorrespondingMemberID, DataType, ReferencedDomainID) VALUES (1, 10, 'Enumeration/Code', 1)");
        Exec(c, $"INSERT INTO mOrdinateMetricRestriction (OrdinateID, HierarchyID) VALUES (100, {restrictionHierarchyId})");
        var releases = currentReleases.Length == 0 ? [release] : currentReleases;
        var id = 0;
        foreach (var r in releases)
        {
            Exec(c, $"INSERT INTO mRelease (ReleaseID, ReleaseCode, IsCurrent) VALUES ({++id}, '{r}', 1)");
        }

        return c;
    }

    /// <summary>
    /// Layout repository (release label <paramref name="release"/>): sheet T_01 asking for
    /// <c>MET(qAA)|DIM(<paramref name="dimMember"/>)</c> in column ordinate 0020, whose metric qAYV is annotated
    /// with hierarchy new_CO5 of qCO.
    /// </summary>
    private SqliteConnection Layouts(string release, string dimMember)
    {
        var path = Path.Join(_dir, $"lay{_counter++}.db");
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        c.Open();
        LayoutRepositorySchema.Create(c);
        Exec(c, "PRAGMA foreign_keys = OFF");
        Exec(c, $"INSERT INTO LayoutFile (FileId, Path, Sha256, FrameworkCode, ModuleCode, ReleaseLabel) VALUES (1, 'p', 's', 'TEST', 'M', '{release}')");
        Exec(c, "INSERT INTO LayoutSheet (SheetId, FileId, SheetName, TableCode) VALUES (1, 1, 'T_01', 'T_01')");
        // Ordinate 1 (code 0020) carries the C-MHR-01 unit; ordinate 2 (code 0010) the C-DPS-01 signature, so
        // that the two checks do not interfere (a cell is only built for ordinate 2).
        Exec(c, "INSERT INTO LayoutOrdinate (OrdinateId, SheetId, Axis, OrdinateCode, IsFallbackCode) VALUES (1, 1, 'X', '0020', 0), (2, 1, 'X', '0010', 0)");
        Exec(c, "INSERT INTO LayoutDeclaration (DeclId, SheetId, Region, CellRef, DomainCode, IsKey) VALUES (1, 1, 'Header', 'D1', 'MET', 0)");
        Exec(c, "INSERT INTO LayoutValue (ValueId, SheetId, DeclId, OrdinateId, DomainCode, MemberCode, Label, CellRef, HierarchyDomainCode, HierarchyCode) VALUES (1, 1, 1, 2, 'MET', 'qAA', 'Type', 'D2', NULL, NULL)");
        Exec(c, "INSERT INTO LayoutDeclaration (DeclId, SheetId, Region, CellRef, DimensionCode, IsKey) VALUES (2, 1, 'Header', 'D3', 'DIM', 0)");
        Exec(c, $"INSERT INTO LayoutValue (ValueId, SheetId, DeclId, OrdinateId, MemberCode, CellRef) VALUES (2, 1, 2, 2, '{dimMember}', 'D4')");
        Exec(c, "INSERT INTO LayoutCell (CellId, SheetId, RowOrdinateId, ColumnOrdinateId, CellRef, IsShaded) VALUES (1, 1, NULL, 2, 'B1', 0)");

        // The C-MHR-01 unit: the metric qAYV of ordinate 0020, annotated with hierarchy new_CO5 of qCO.
        Exec(c, "INSERT INTO LayoutDeclaration (DeclId, SheetId, Region, CellRef, DomainCode, IsKey) VALUES (3, 1, 'Header', 'E1', 'MET', 0)");
        Exec(c, "INSERT INTO LayoutValue (ValueId, SheetId, DeclId, OrdinateId, DomainCode, MemberCode, Label, CellRef, HierarchyDomainCode, HierarchyCode) VALUES (3, 1, 3, 1, 'MET', 'qAYV', 'Type', 'E2', 'qCO', 'new_CO5')");
        return c;
    }

    private static (CheckResult Dps, CheckResult Mhr) Run(SqliteConnection g, SqliteConnection l, List<KnownExceptions.Outcome> sink)
    {
        var results = PlaneCChecks.Run(g, l, sink).ToList();
        return (Assert.Single(results, r => r.Id == "C-DPS-01"), Assert.Single(results, r => r.Id == "C-MHR-01"));
    }

    // ---- the embedded censuses ----

    [Fact]
    public void TheEmbeddedCensuses_AreOnePerRelease_AndTheOneOfAReleaseIsFoundByIt()
    {
        var releases = KnownExceptions.PlaneCCensusReleases;

        Assert.NotEmpty(releases);
        Assert.Equal(releases.Distinct(StringComparer.Ordinal).Count(), releases.Count);
        foreach (var release in releases)
        {
            Assert.Equal(release, KnownExceptions.PlaneCCensusForRelease(release)!.CorpusRelease);
        }

        // Positive control: a release with no census is not served another release's one.
        Assert.Null(KnownExceptions.PlaneCCensusForRelease("9.9"));
        Assert.DoesNotContain("9.9", releases);
    }

    [Fact]
    public void LoadPlaneCCensuses_TwoFilesForTheSameRelease_AreAnError_NamingBoth()
    {
        const string a = "# CorpusRelease: 7.1\nTableCode\tType\tLayoutSignature\n";
        const string b = "# CorpusRelease: 7.1 -- another measurement\nTableCode\tType\tLayoutSignature\n";

        var error = Assert.Throws<InvalidOperationException>(() => KnownExceptions.LoadPlaneCCensuses(
            [("plane-c-known-divergences-7.1.tsv", a), ("plane-c-known-divergences-7.1-bis.tsv", b)]));

        Assert.Contains("plane-c-known-divergences-7.1.tsv", error.Message, StringComparison.Ordinal);
        Assert.Contains("plane-c-known-divergences-7.1-bis.tsv", error.Message, StringComparison.Ordinal);
        Assert.Contains("7.1", error.Message, StringComparison.Ordinal);

        // Positive control: two DIFFERENT releases load, and each keeps its own entries.
        const string c = "# CorpusRelease: 7.2\nTableCode\tType\tLayoutSignature\nT_09\tcontradicts\tDIM(m1)|MET(qAA)\n";
        var both = KnownExceptions.LoadPlaneCCensuses([("plane-c-known-divergences-7.1.tsv", a), ("plane-c-known-divergences-7.2.tsv", c)]);
        Assert.Equal(["7.1", "7.2"], both.Select(x => x.CorpusRelease));
        Assert.Empty(both[0].Exceptions);
        Assert.Equal("T_09\tDIM(m1)|MET(qAA)", Assert.Single(both[1].Exceptions).BusinessKey);
    }

    [Fact]
    public void LoadPlaneCCensuses_AFileWithoutItsCorpusReleaseHeader_IsAnError()
    {
        var error = Assert.Throws<InvalidOperationException>(() => KnownExceptions.LoadPlaneCCensuses(
            [("plane-c-known-divergences-x.tsv", "TableCode\tType\tLayoutSignature\nT_09\tcontradicts\tDIM(m1)\n")]));

        Assert.Contains("plane-c-known-divergences-x.tsv", error.Message, StringComparison.Ordinal);
        Assert.Contains("CorpusRelease", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReasonOfACensusEntry_NamesTheFileItWasReadFrom()
    {
        var census = KnownExceptions.LoadPlaneCCensuses(
            [("plane-c-known-divergences-7.1.tsv", "# CorpusRelease: 7.1\nT_09\tcontradicts\tDIM(m1)|MET(qAA)\n")]).Single();

        Assert.Contains("plane-c-known-divergences-7.1.tsv", Assert.Single(census.Exceptions).Reason, StringComparison.Ordinal);
    }

    // ---- C-DPS-01 / C-MHR-01 against the census of the output's release ----

    [Fact]
    public void ARelease_WithNoCensus_IsEvaluatedWithAnEmptyCensus_NotSkipped_AndEveryViolationFails()
    {
        Assert.Null(KnownExceptions.PlaneCCensusForRelease("9.9")); // the premise
        using var g = Output("9.9", restrictionHierarchyId: 2); // MHR: restricted to new_CO4, the layout says new_CO5
        using var l = Layouts("9.9", dimMember: "m2"); // DPS: the layout asks DIM(m2), we emit DIM(m1)
        var sink = new List<KnownExceptions.Outcome>();

        var (dps, mhr) = Run(g, l, sink);

        Assert.Equal(CheckStatus.Fail, dps.Status);
        Assert.Equal(1, dps.Failed);
        Assert.Equal(1, dps.Examined);
        Assert.Contains("EMPTY census", dps.Statement, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Fail, mhr.Status);
        Assert.Equal(1, mhr.Failed);
        Assert.Contains("EMPTY census", mhr.Statement, StringComparison.Ordinal);

        // Nothing can expire under an empty census: no census entry is even evaluated.
        Assert.DoesNotContain(sink, o => o.Exception.Id is "DD-23" or "DD-27");
    }

    [Fact]
    public void ARelease_WithNoCensus_AndNoViolation_Passes_ProvingTheEmptyCensusIsEvaluatedAndNotJustFailing()
    {
        using var g = Output("9.9", restrictionHierarchyId: 1);
        using var l = Layouts("9.9", dimMember: "m1");

        var (dps, mhr) = Run(g, l, []);

        Assert.Equal(CheckStatus.Pass, dps.Status);
        Assert.Equal(1, dps.Examined);
        Assert.Equal(0, dps.Failed);
        Assert.Equal(CheckStatus.Pass, mhr.Status);
        Assert.Equal(0, mhr.Failed);
    }

    [Fact]
    public void ARelease_WithACensus_ConsultsIt_SoItsEntriesAreEvaluated_AndStaleOnesFail()
    {
        var release = KnownExceptions.PlaneCCensusReleases.First();
        var declared = KnownExceptions.PlaneCCensusForRelease(release)!.Exceptions.Count(e => e.Id == "DD-23");
        Assert.True(declared > 0, $"The census of release {release} declares no C-DPS-01 entry: the control would be vacuous.");
        using var g = Output(release, restrictionHierarchyId: 1);
        using var l = Layouts(release, dimMember: "m1"); // no violation of ours: every declared entry is stale here
        var sink = new List<KnownExceptions.Outcome>();

        var (dps, _) = Run(g, l, sink);

        var outcomes = sink.Where(o => o.Exception.Id == "DD-23").ToList();
        Assert.Equal(declared, outcomes.Count);
        Assert.All(outcomes, o => Assert.True(o.Stale));
        Assert.Equal(CheckStatus.Fail, dps.Status);
        Assert.Equal(declared, dps.Failed);
        Assert.DoesNotContain("EMPTY census", dps.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutputWithNoSingleRelease_Dpm10Shape_ConsultsNoCensus_AndEvaluatesTheRawViolations()
    {
        using var g = Output("4.2", restrictionHierarchyId: 2, "4.1", "4.2"); // IsCurrent = 1 in two rows
        using var l = Layouts("4.2", dimMember: "m2");
        var sink = new List<KnownExceptions.Outcome>();

        var (dps, mhr) = Run(g, l, sink);

        Assert.Equal(CheckStatus.Fail, dps.Status);
        Assert.Equal(1, dps.Failed);
        Assert.Contains("NOT CONSULTED", dps.Statement, StringComparison.Ordinal);
        Assert.DoesNotContain("EMPTY census", dps.Statement, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Fail, mhr.Status);
        Assert.Contains("NOT CONSULTED", mhr.Statement, StringComparison.Ordinal);
        Assert.DoesNotContain(sink, o => o.Exception.Id is "DD-23" or "DD-27");
    }
}
