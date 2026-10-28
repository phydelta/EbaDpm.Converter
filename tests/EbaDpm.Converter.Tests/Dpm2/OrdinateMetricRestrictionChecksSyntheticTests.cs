using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Core.Validation.Checks;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Plane A checks of the extension table <c>mOrdinateMetricRestriction</c> (issue 61): A-OMR-01..04,
/// I-REF-17.1..3, A-VAC-03 (46 tables, the extension named) and B-SCH-01 (the extension is the only
/// table tolerated beyond the reference). Rule: docs/validation.md and docs/target-schema.md,
/// "Extension tables". Each check is run on a small valid database (it must pass AND examine a row,
/// so that a pass is not vacuous) and on a database broken in exactly the way the check names
/// (positive control: it must fail).
/// </summary>
public sealed class OrdinateMetricRestrictionChecksSyntheticTests : IDisposable
{
    private readonly string _dir;

    public OrdinateMetricRestrictionChecksSyntheticTests()
    {
        _dir = Path.Join(Path.GetTempPath(), $"EbaDpm.OmrChecks_{Environment.ProcessId}_{Guid.NewGuid():N}");
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

    private SqliteConnection Create(string name, bool withValidRestriction = true, string metricDataType = "Enumeration/Code")
    {
        var path = Path.Join(_dir, name + ".db");
        SchemaCreator.Create(path, overwrite: true);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite }.ToString());
        c.Open();
        SqlHelpers.Execute(c, "PRAGMA foreign_keys = OFF");

        if (withValidRestriction)
        {
            // Domain QCO (1) with hierarchy new_CO5 (1) and members qx2000 (20), qx2015 (21) as nodes.
            // Metric qAYV is member 10 (MET domain 2), enumerated, domain QCO, no hierarchy of its own.
            Exec(c, "INSERT INTO mDomain (DomainID, DomainCode) VALUES (1, 'QCO'), (2, 'MET')");
            Exec(c, "INSERT INTO mMember (MemberID, DomainID, MemberCode) VALUES (10, 2, 'qAYV'), (20, 1, 'qx2000'), (21, 1, 'qx2015'), (22, 1, 'qx2099')");
            Exec(c, "INSERT INTO mDimension (DimensionID, DimensionCode, DimensionXBRLCode, DomainID) VALUES (9999, 'MET', 'MET', 2)");
            Exec(c, $"INSERT INTO mMetric (MetricID, CorrespondingMemberID, DataType, ReferencedDomainID) VALUES (1, 10, '{metricDataType}', 1)");
            Exec(c, "INSERT INTO mHierarchy (HierarchyID, HierarchyCode, DomainID) VALUES (1, 'new_CO5', 1)");
            Exec(c, "INSERT INTO mHierarchyNode (HierarchyNodeID, HierarchyID, MemberID) VALUES (1, 1, 20), (2, 1, 21)");
            Exec(c, "INSERT INTO mAxisOrdinate (OrdinateID, OrdinateCode) VALUES (100, '0020')");
            Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (100, 9999, 10)");
            Exec(c, "INSERT INTO mOrdinateMetricRestriction (OrdinateID, HierarchyID, HierarchyStartingMemberID, IsStartingMemberIncluded) VALUES (100, 1, NULL, NULL)");
        }

        return c;
    }

    private static void Exec(SqliteConnection c, string sql) => SqlHelpers.Execute(c, sql);

    private static CheckResult Omr(SqliteConnection c, string id, ValidationSourceModel model = ValidationSourceModel.Dpm2)
        => First(CellChecks.Run(c, model), id);

    /// <summary>
    /// The check runners are lazy iterators over the whole battery: the check under test is taken
    /// as soon as it is yielded, so that the other checks (which need a complete database, for
    /// example the axis closure) are never evaluated over these minimal fixtures. The id must be
    /// unique in the battery, asserted by the cheap fact that it is found first.
    /// </summary>
    private static CheckResult First(IEnumerable<CheckResult> results, string id)
        => results.First(r => r.Id == id);

    private static void AssertPasses(CheckResult r)
    {
        Assert.Equal(CheckStatus.Pass, r.Status);
        Assert.Equal(1, r.Examined); // not vacuous: the one restriction row was examined
        Assert.Equal(0, r.Failed);
        Assert.Equal(ValidationLayer.Critical, r.Layer);
    }

    private static void AssertFails(CheckResult r, long failed = 1)
    {
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Equal(failed, r.Failed);
    }

    // ---- valid database: every check passes and examined the row ----

    [Theory]
    [InlineData("A-OMR-01")]
    [InlineData("A-OMR-02")]
    [InlineData("A-OMR-03")]
    [InlineData("A-OMR-04")]
    public void ValidDatabase_Passes_AndExaminesTheRow(string id)
    {
        using var c = Create("valid-" + id);
        AssertPasses(Omr(c, id));
    }

    [Fact]
    public void StartingMember_ThatIsANodeAndIncluded_Passes()
    {
        using var c = Create("start-ok");
        Exec(c, "UPDATE mOrdinateMetricRestriction SET HierarchyStartingMemberID = 20, IsStartingMemberIncluded = 1");
        AssertPasses(Omr(c, "A-OMR-04"));
    }

    [Fact]
    public void EmptyTable_ExaminesNothing_SoAZeroIsVisibleAsSuch()
    {
        using var c = Create("empty", withValidRestriction: false);
        var r = Omr(c, "A-OMR-01");
        Assert.Equal(0, r.Examined);
    }

    // ---- A-OMR-01: exactly one MET categorisation ----

    [Fact]
    public void AOmr01_OrdinateWithoutMetCategorisation_Fails()
    {
        using var c = Create("omr01-none");
        Exec(c, "DELETE FROM mOrdinateCategorisation WHERE OrdinateID = 100");
        AssertFails(Omr(c, "A-OMR-01"));
    }

    [Fact]
    public void AOmr01_OrdinateWithTwoMetCategorisations_Fails()
    {
        using var c = Create("omr01-two");
        Exec(c, "INSERT INTO mDimension (DimensionID, DimensionCode, DimensionXBRLCode, DomainID) VALUES (9998, 'MET2', 'MET', 2)");
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (100, 9998, 10)");
        AssertFails(Omr(c, "A-OMR-01"));
    }

    [Fact]
    public void AOmr01_OrdinateWithOnlyANonMetCategorisation_Fails()
    {
        using var c = Create("omr01-other");
        Exec(c, "INSERT INTO mDimension (DimensionID, DimensionCode, DimensionXBRLCode, DomainID) VALUES (5, 'XYZ', 'eba_dim:XYZ', 1)");
        Exec(c, "UPDATE mOrdinateCategorisation SET DimensionID = 5");
        AssertFails(Omr(c, "A-OMR-01"));
    }

    // ---- A-OMR-02: the metric is enumerated, spelled per source model ----

    [Fact]
    public void AOmr02_Dpm2_RequiresEnumerationCode()
    {
        using var ok = Create("omr02-dpm2-ok");
        AssertPasses(Omr(ok, "A-OMR-02", ValidationSourceModel.Dpm2));

        using var bad = Create("omr02-dpm2-code", metricDataType: "Code");
        AssertFails(Omr(bad, "A-OMR-02", ValidationSourceModel.Dpm2));

        using var integer = Create("omr02-dpm2-int", metricDataType: "Integer");
        AssertFails(Omr(integer, "A-OMR-02", ValidationSourceModel.Dpm2));
    }

    [Fact]
    public void AOmr02_Dpm1_RequiresCode()
    {
        using var ok = Create("omr02-dpm1-ok", metricDataType: "Code");
        AssertPasses(Omr(ok, "A-OMR-02", ValidationSourceModel.Dpm1));

        using var bad = Create("omr02-dpm1-enum", metricDataType: "Enumeration/Code");
        AssertFails(Omr(bad, "A-OMR-02", ValidationSourceModel.Dpm1));

        using var integer = Create("omr02-dpm1-int", metricDataType: "Integer");
        AssertFails(Omr(integer, "A-OMR-02", ValidationSourceModel.Dpm1));
    }

    [Fact]
    public void AOmr02_MetricRowMissing_Fails()
    {
        using var c = Create("omr02-nometric");
        Exec(c, "DELETE FROM mMetric");
        AssertFails(Omr(c, "A-OMR-02"));
    }

    // ---- A-OMR-03: the domain of the hierarchy is the metric's ReferencedDomainID ----

    [Fact]
    public void AOmr03_HierarchyOfAnotherDomain_Fails()
    {
        using var c = Create("omr03-other-domain");
        Exec(c, "INSERT INTO mDomain (DomainID, DomainCode) VALUES (3, 'QAB')");
        Exec(c, "INSERT INTO mHierarchy (HierarchyID, HierarchyCode, DomainID) VALUES (2, 'other', 3)");
        Exec(c, "UPDATE mOrdinateMetricRestriction SET HierarchyID = 2");
        AssertFails(Omr(c, "A-OMR-03"));
    }

    [Fact]
    public void AOmr03_MetricWithoutReferencedDomain_Fails()
    {
        using var c = Create("omr03-null-domain");
        Exec(c, "UPDATE mMetric SET ReferencedDomainID = NULL");
        AssertFails(Omr(c, "A-OMR-03"));
    }

    // ---- A-OMR-04: starting member both-or-none, and a node of the hierarchy ----

    [Fact]
    public void AOmr04_StartingMemberWithoutInclusionFlag_Fails()
    {
        using var c = Create("omr04-no-flag");
        Exec(c, "UPDATE mOrdinateMetricRestriction SET HierarchyStartingMemberID = 20, IsStartingMemberIncluded = NULL");
        AssertFails(Omr(c, "A-OMR-04"));
    }

    [Fact]
    public void AOmr04_InclusionFlagWithoutStartingMember_Fails()
    {
        using var c = Create("omr04-no-member");
        Exec(c, "UPDATE mOrdinateMetricRestriction SET HierarchyStartingMemberID = NULL, IsStartingMemberIncluded = 1");
        AssertFails(Omr(c, "A-OMR-04"));
    }

    [Fact]
    public void AOmr04_StartingMemberOutsideTheHierarchy_Fails()
    {
        using var c = Create("omr04-not-node");
        // Member 22 is in the domain but is not a node of hierarchy 1.
        Exec(c, "UPDATE mOrdinateMetricRestriction SET HierarchyStartingMemberID = 22, IsStartingMemberIncluded = 1");
        AssertFails(Omr(c, "A-OMR-04"));
    }

    // ---- I-REF-17.1..3 ----

    private static CheckResult Ref(SqliteConnection c, string id)
        => First(IntegrityChecks.Run(c, ValidationSourceModel.Dpm2, []), id);

    [Theory]
    [InlineData("I-REF-17.1")]
    [InlineData("I-REF-17.2")]
    [InlineData("I-REF-17.3")]
    public void IRef17_ValidDatabase_Passes(string id)
    {
        using var c = Create("iref-ok");
        Exec(c, "UPDATE mOrdinateMetricRestriction SET HierarchyStartingMemberID = 20, IsStartingMemberIncluded = 1");
        var r = Ref(c, id);
        Assert.Equal(CheckStatus.Pass, r.Status);
        Assert.Equal(1, r.Examined);
    }

    [Fact]
    public void IRef17_1_OrdinateThatDoesNotExist_Fails()
    {
        using var c = Create("iref-ord");
        Exec(c, "DELETE FROM mAxisOrdinate WHERE OrdinateID = 100");
        AssertFails(Ref(c, "I-REF-17.1"));
    }

    [Fact]
    public void IRef17_2_HierarchyThatDoesNotExist_Fails()
    {
        using var c = Create("iref-hier");
        Exec(c, "UPDATE mOrdinateMetricRestriction SET HierarchyID = 777");
        AssertFails(Ref(c, "I-REF-17.2"));
    }

    [Fact]
    public void IRef17_3_StartingMemberThatDoesNotExist_Fails()
    {
        using var c = Create("iref-member");
        Exec(c, "UPDATE mOrdinateMetricRestriction SET HierarchyStartingMemberID = 888, IsStartingMemberIncluded = 1");
        AssertFails(Ref(c, "I-REF-17.3"));
    }

    // ---- A-VAC-03: 45 + the extension, by name ----

    private static CheckResult Vac03(SqliteConnection c)
        => First(IntegrityChecks.Run(c, ValidationSourceModel.Dpm2, []), "A-VAC-03");

    [Fact]
    public void AVac03_FullSchema_Has46Tables_AndPasses()
    {
        using var c = Create("vac-ok", withValidRestriction: false);
        Assert.Equal(46, SqlHelpers.Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"));
        Assert.Equal(CheckStatus.Pass, Vac03(c).Status);
    }

    [Fact]
    public void AVac03_ExtensionTableMissing_Fails()
    {
        using var c = Create("vac-missing", withValidRestriction: false);
        Exec(c, "DROP TABLE mOrdinateMetricRestriction");
        Assert.Equal(CheckStatus.Fail, Vac03(c).Status);
    }

    [Fact]
    public void AVac03_ExtensionReplacedByAnotherTable_StillFails_ByName()
    {
        // 46 tables again, but the extension is not the one present: the count alone would pass.
        using var c = Create("vac-renamed", withValidRestriction: false);
        Exec(c, "DROP TABLE mOrdinateMetricRestriction");
        Exec(c, "CREATE TABLE xSomethingElse (Id INTEGER)");
        Assert.Equal(46, SqlHelpers.Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"));
        Assert.Equal(CheckStatus.Fail, Vac03(c).Status);
    }

    [Fact]
    public void AVac03_AnExtraTable_Fails()
    {
        using var c = Create("vac-extra", withValidRestriction: false);
        Exec(c, "CREATE TABLE xSomethingElse (Id INTEGER)");
        Assert.Equal(CheckStatus.Fail, Vac03(c).Status);
    }

    // ---- B-SCH-01: the extension is the only table the output may have beyond the reference ----

    private SqliteConnection CreateReference(string name)
    {
        var c = Create(name, withValidRestriction: false);
        Exec(c, "DROP TABLE mOrdinateMetricRestriction"); // a reference export has the 45 standard tables only
        return c;
    }

    [Fact]
    public void BSch01_ExtensionTable_IsNotReportedAsAnExtraTable()
    {
        using var generated = Create("sch-gen", withValidRestriction: false);
        using var reference = CreateReference("sch-ref");

        var r = SchemaCensusChecks.Run(generated, reference, ReferenceRole.Reference42);

        Assert.Equal(CheckStatus.Pass, r.Status);
        Assert.Equal(0, r.Failed);
        Assert.Equal(45, r.Examined);
    }

    [Fact]
    public void BSch01_AnyOtherExtraTable_Fails_ByName()
    {
        using var generated = Create("sch-gen-extra", withValidRestriction: false);
        Exec(generated, "CREATE TABLE xSomethingElse (Id INTEGER)");
        using var reference = CreateReference("sch-ref-extra");

        var r = SchemaCensusChecks.Run(generated, reference, ReferenceRole.Reference42);

        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Contains(r.Samples, s => s.BusinessKey.Contains("xSomethingElse", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Samples, s => s.BusinessKey.Contains("mOrdinateMetricRestriction", StringComparison.Ordinal));
    }

    [Fact]
    public void BSch01_ExtensionTable_NameIsMatchedExactly_NotByPattern()
    {
        using var generated = Create("sch-gen-pattern", withValidRestriction: false);
        Exec(generated, "CREATE TABLE mOrdinateMetricRestriction2 (Id INTEGER)");
        using var reference = CreateReference("sch-ref-pattern");

        var r = SchemaCensusChecks.Run(generated, reference, ReferenceRole.Reference42);

        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Contains(r.Samples, s => s.BusinessKey.Contains("mOrdinateMetricRestriction2", StringComparison.Ordinal));
    }

    // ---- Output without the extension table: the 7 checks are Skipped, A-VAC-03 fails ----

    [Fact]
    public void WithoutTheExtensionTable_TheSevenChecksAreSkipped_WithTheReason_AndAVac03Fails()
    {
        using var c = Create("no-ext");
        Exec(c, "DROP TABLE mOrdinateMetricRestriction");

        var cell = CellChecks.Run(c, ValidationSourceModel.Dpm2).ToList();
        var integrity = IntegrityChecks.Run(c, ValidationSourceModel.Dpm2, []).ToList();

        foreach (var id in new[] { "A-OMR-01", "A-OMR-02", "A-OMR-03", "A-OMR-04" })
        {
            var r = Assert.Single(cell, x => x.Id == id);
            Assert.Equal(CheckStatus.Skipped, r.Status);
            Assert.Equal(SqlHelpers.ExtensionTableAbsentReason, r.SkipReason);
        }

        foreach (var id in new[] { "I-REF-17.1", "I-REF-17.2", "I-REF-17.3" })
        {
            var r = Assert.Single(integrity, x => x.Id == id);
            Assert.Equal(CheckStatus.Skipped, r.Status);
            Assert.Equal(SqlHelpers.ExtensionTableAbsentReason, r.SkipReason);
        }

        Assert.Equal(CheckStatus.Fail, Assert.Single(integrity, x => x.Id == "A-VAC-03").Status);
    }
}
