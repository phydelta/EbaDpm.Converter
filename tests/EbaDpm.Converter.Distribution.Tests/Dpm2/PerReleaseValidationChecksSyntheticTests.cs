using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Distribution.Validation.Checks;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Distribution.Tests.Dpm2;

/// <summary>
/// White-box tests on MINIMAL SQLite databases (the empty 45-table schema plus a handful of rows)
/// for the validation changes that came with one-SQLite-file-per-release (3.4, 3.5 and 4.0 files
/// are produced for the first time): A-SEN-04 only for DPM 1.0, A-VAC-01.mDomainUnion as an
/// informative census in DPM 2.0, and the dedicated A-OCA-10 (AO-4) and I-SIG-07 (AO-5) checks
/// that consume origin-anomaly exceptions keyed by taxonomy. Expected values come from
/// <c>docs/validation.md</c> and <c>docs/mapping-dpm2.md</c>. The declared keys used here are the
/// literal ones of <c>KnownExceptions</c>; they are asserted by name, never by pattern.
/// </summary>
public sealed class PerReleaseValidationChecksSyntheticTests : IDisposable
{
    private const string Ao4Key = "pillar3 4.0|K_04.00.b|eba_dim_3.4:PDR";
    private const string Ao5Table = "C_96.05.2";

    private readonly string _dir;
    private int _nextId = 1;

    public PerReleaseValidationChecksSyntheticTests()
    {
        _dir = Path.Join(Path.GetTempPath(), $"EbaDpm.PerReleaseChecks_{Environment.ProcessId}_{Guid.NewGuid():N}");
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
        var path = Path.Join(_dir, fileName);
        SchemaCreator.Create(path, overwrite: true);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();
        SqlHelpers.Execute(connection, "PRAGMA foreign_keys = OFF");
        return connection;
    }

    private static void Exec(SqliteConnection c, string sql) => SqlHelpers.Execute(c, sql);

    // -----------------------------------------------------------------------------------------
    // A-SEN-04: a DPM 1.0 source sentinel; not emitted for DPM 2.0.
    // -----------------------------------------------------------------------------------------

    private static List<CheckResult> RunDictionary(SqliteConnection c, ValidationSourceModel model)
        => DictionaryChecks.Run(c, model, []).ToList();

    [Fact]
    public void ASen04_Dpm1_WithARowOfMemberId999_StillFails()
    {
        using var c = CreateAndOpen("asen04-dpm1-bug.db");
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (1, 1, 999), (2, 1, 5)");

        var asen04 = Assert.Single(RunDictionary(c, ValidationSourceModel.Dpm1), r => r.Id == "A-SEN-04");

        Assert.Equal(CheckStatus.Fail, asen04.Status);
        Assert.Equal(ValidationLayer.Critical, asen04.Layer);
        Assert.Equal(2, asen04.Examined);
        Assert.Equal(1, asen04.Failed);
    }

    [Fact]
    public void ASen04_Dpm1_WithoutTheSentinel_Passes()
    {
        using var c = CreateAndOpen("asen04-dpm1-ok.db");
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (1, 1, 5)");

        var asen04 = Assert.Single(RunDictionary(c, ValidationSourceModel.Dpm1), r => r.Id == "A-SEN-04");

        Assert.Equal(CheckStatus.Pass, asen04.Status);
        Assert.Equal(1, asen04.Examined);
    }

    /// <summary>The same data that fails for DPM 1.0 (control above): in DPM 2.0 999 is an ordinary minted MemberID.</summary>
    [Fact]
    public void ASen04_Dpm2_IsNotEmitted_EvenWithARowOfMemberId999()
    {
        using var c = CreateAndOpen("asen04-dpm2.db");
        Exec(c, "INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES (1, 1, 999), (2, 1, 5)");

        var results = RunDictionary(c, ValidationSourceModel.Dpm2);

        Assert.DoesNotContain(results, r => r.Id == "A-SEN-04");
        // The neighbouring sentinel checks are still emitted: the absence is specific to A-SEN-04.
        Assert.Contains(results, r => r.Id == "A-SEN-03");
        Assert.Contains(results, r => r.Id == "A-SEN-05");
    }

    // -----------------------------------------------------------------------------------------
    // A-VAC-01.mDomainUnion
    // -----------------------------------------------------------------------------------------

    private static CheckResult DomainUnion(SqliteConnection c, ValidationSourceModel model)
        => Assert.Single(EmptyTableChecks.Run(c, model).ToList(), r => r.Id == "A-VAC-01.mDomainUnion");

    [Fact]
    public void AVac01DomainUnion_Dpm2_WithZeroRows_IsAnInformativeCensus_AndNeverFails()
    {
        using var c = CreateAndOpen("avac01-dpm2-zero.db");

        var result = DomainUnion(c, ValidationSourceModel.Dpm2);

        Assert.Equal(ValidationLayer.Informative, result.Layer);
        Assert.NotEqual(CheckStatus.Fail, result.Status);
        Assert.Equal(0, result.Failed);
        Assert.Contains(result.Samples, s => s.BusinessKey.Contains("0 row", StringComparison.Ordinal));
    }

    [Fact]
    public void AVac01DomainUnion_Dpm2_WithRows_IsStillInformative_AndStatesTheCount()
    {
        using var c = CreateAndOpen("avac01-dpm2-rows.db");
        Exec(c, "INSERT INTO mDomainUnion (UnionDomainID, UnitedDomainID) VALUES (1, 2), (1, 3)");

        var result = DomainUnion(c, ValidationSourceModel.Dpm2);

        Assert.Equal(ValidationLayer.Informative, result.Layer);
        Assert.Equal(0, result.Failed);
        Assert.Contains(result.Samples, s => s.BusinessKey.Contains("2 row", StringComparison.Ordinal));
    }

    [Fact]
    public void AVac01DomainUnion_Dpm1_IsUnchanged_CriticalAndPassesWhenEmpty()
    {
        using var c = CreateAndOpen("avac01-dpm1-empty.db");

        var result = DomainUnion(c, ValidationSourceModel.Dpm1);

        Assert.Equal(ValidationLayer.Critical, result.Layer);
        Assert.Equal(CheckStatus.Pass, result.Status);
    }

    /// <summary>Positive control of the DPM 1.0 check: a row makes it fail (the DPM 2.0 behaviour is not leaking into it).</summary>
    [Fact]
    public void AVac01DomainUnion_Dpm1_WithARow_StillFails()
    {
        using var c = CreateAndOpen("avac01-dpm1-bug.db");
        Exec(c, "INSERT INTO mDomainUnion (UnionDomainID, UnitedDomainID) VALUES (1, 2)");

        var result = DomainUnion(c, ValidationSourceModel.Dpm1);

        Assert.Equal(ValidationLayer.Critical, result.Layer);
        Assert.Equal(CheckStatus.Fail, result.Status);
    }

    // -----------------------------------------------------------------------------------------
    // A-OCA-10 / AO-4: key TaxonomyCode|TableCode|DimensionXBRLCode, two-sided, absent = not applicable.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A table of a taxonomy with an X axis and a Y axis. When <paramref name="dimensionOnBothAxes"/>
    /// the dimension categorises one ordinate on each (the anomaly); otherwise only on X.
    /// </summary>
    private void AddAxisWorld(SqliteConnection c, string taxonomy, string table, string dimension, bool dimensionOnBothAxes)
    {
        var id = _nextId++;
        var axisX = id * 10 + 1;
        var axisY = id * 10 + 2;
        var ordX = id * 100 + 1;
        var ordY = id * 100 + 2;
        Exec(c, $"INSERT INTO mTaxonomy (TaxonomyID, TaxonomyCode) VALUES ({id}, '{taxonomy}')");
        Exec(c, $"INSERT INTO mTable (TableID, TableCode) VALUES ({id}, '{table}')");
        Exec(c, $"INSERT INTO mTaxonomyTable (TaxonomyID, TableID, AnnotatedTableID) VALUES ({id}, {id}, {id})");
        Exec(c, $"INSERT INTO mDimension (DimensionID, DimensionCode, DimensionXBRLCode, DomainID) VALUES ({id}, 'D{id}', '{dimension}', 1)");
        Exec(c, $"INSERT INTO mAxis (AxisID, AxisOrientation) VALUES ({axisX}, 'X'), ({axisY}, 'Y')");
        Exec(c, $"INSERT INTO mTableAxis (AxisID, TableID, [Order]) VALUES ({axisX}, {id}, 1), ({axisY}, {id}, 2)");
        Exec(c, $"INSERT INTO mAxisOrdinate (AxisID, OrdinateID) VALUES ({axisX}, {ordX}), ({axisY}, {ordY})");
        Exec(c, $"INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES ({ordX}, {id}, 1)");
        if (dimensionOnBothAxes)
        {
            Exec(c, $"INSERT INTO mOrdinateCategorisation (OrdinateID, DimensionID, MemberID) VALUES ({ordY}, {id}, 1)");
        }
    }

    private static (CheckResult Check, List<KnownExceptions.Outcome> Outcomes) RunAoca10(SqliteConnection c)
    {
        var sink = new List<KnownExceptions.Outcome>();
        var results = CellChecks.Run(c, ValidationSourceModel.Dpm2, sink).ToList();
        return (Assert.Single(results, r => r.Id == "A-OCA-10"), sink);
    }

    private static KnownExceptions.Outcome Outcome(List<KnownExceptions.Outcome> outcomes, string key)
        => Assert.Single(outcomes, o => o.Exception.BusinessKey == key);

    [Fact]
    public void AOca10_DeclaredKey_PresentAndViolating_IsApplied_AndThePassIsNotStale()
    {
        using var c = CreateAndOpen("aoca10-applied.db");
        AddAxisWorld(c, "pillar3 4.0", "K_04.00.b", "eba_dim_3.4:PDR", dimensionOnBothAxes: true);

        var (check, outcomes) = RunAoca10(c);

        Assert.Equal(CheckStatus.Pass, check.Status);
        Assert.Equal(0, check.Failed);
        Assert.Equal(1, check.Examined);
        var applied = Outcome(outcomes, Ao4Key);
        Assert.Equal("AO-4", applied.Exception.Id);
        Assert.True(applied.AppliesToThisReference);
        Assert.Equal(1, applied.Matched);
        Assert.False(applied.Stale);
    }

    [Fact]
    public void AOca10_DeclaredKey_PresentButNoLongerViolating_IsStale_AndFails()
    {
        using var c = CreateAndOpen("aoca10-stale.db");
        AddAxisWorld(c, "pillar3 4.0", "K_04.00.b", "eba_dim_3.4:PDR", dimensionOnBothAxes: false);

        var (check, outcomes) = RunAoca10(c);

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal(ValidationLayer.Critical, check.Layer);
        Assert.Equal(1, check.Failed);
        Assert.Contains(check.Samples, s => s.BusinessKey.Contains(Ao4Key, StringComparison.Ordinal));
        var stale = Outcome(outcomes, Ao4Key);
        Assert.True(stale.AppliesToThisReference);
        Assert.True(stale.Stale);
    }

    [Fact]
    public void AOca10_DeclaredKeys_AbsentFromTheFile_DoNotApply_AreNotStale_AndDoNotFail()
    {
        using var c = CreateAndOpen("aoca10-absent.db");
        // A file of another release: only a clean table that is not any declared key.
        AddAxisWorld(c, "corep 3.5", "C_01.00", "eba_dim_3.4:PDR", dimensionOnBothAxes: false);

        var (check, outcomes) = RunAoca10(c);

        Assert.Equal(CheckStatus.Pass, check.Status);
        Assert.Equal(0, check.Failed);
        var ao4 = outcomes.Where(o => o.Exception.Id == "AO-4").ToList();
        Assert.Equal(4, ao4.Count);
        Assert.All(ao4, o =>
        {
            Assert.False(o.AppliesToThisReference);
            Assert.False(o.Stale);
            Assert.Equal(0, o.Matched);
        });
    }

    /// <summary>Positive control: the same violation under an UNDECLARED table still fails.</summary>
    [Fact]
    public void AOca10_UndeclaredViolation_StillFails()
    {
        using var c = CreateAndOpen("aoca10-undeclared.db");
        AddAxisWorld(c, "pillar3 4.0", "K_99.00", "eba_dim_3.4:PDR", dimensionOnBothAxes: true);

        var (check, _) = RunAoca10(c);

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal(1, check.Failed);
        Assert.Contains(check.Samples, s => s.BusinessKey == "pillar3 4.0|K_99.00|eba_dim_3.4:PDR");
    }

    /// <summary>The key includes the taxonomy: the same table code and dimension under another taxonomy is NOT covered.</summary>
    [Theory]
    [InlineData("pillar3 4.1")]
    [InlineData("pillar3 3.5")]
    [InlineData("corep 4.0")]
    public void AOca10_SameTableAndDimensionUnderAnotherTaxonomy_IsNotCovered(string taxonomy)
    {
        using var c = CreateAndOpen("aoca10-other-taxonomy.db");
        AddAxisWorld(c, taxonomy, "K_04.00.b", "eba_dim_3.4:PDR", dimensionOnBothAxes: true);

        var (check, outcomes) = RunAoca10(c);

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal(1, check.Failed);
        Assert.Contains(check.Samples, s => s.BusinessKey == $"{taxonomy}|K_04.00.b|eba_dim_3.4:PDR");
        Assert.False(Outcome(outcomes, Ao4Key).AppliesToThisReference);
    }

    [Fact]
    public void AOca10_TheFourDeclaredKeys_AreExactlyTheOnesOfAo4()
    {
        using var c = CreateAndOpen("aoca10-keys.db");

        var (_, outcomes) = RunAoca10(c);

        Assert.Equal(
            [
                "pillar3 3.4|K_04.00.b|eba_dim_3.4:PDR",
                "pillar3 4.0|K_04.00.b|eba_dim_3.4:PDR",
                "pillar3 4.0|K_05.00.b|eba_dim_3.4:BAS",
                "pillar3 4.0|K_26.00.b|eba_dim_3.4:TRI",
            ],
            outcomes.Where(o => o.Exception.Id == "AO-4").Select(o => o.Exception.BusinessKey).Order(StringComparer.Ordinal).ToList());
    }

    // -----------------------------------------------------------------------------------------
    // I-SIG-07 / AO-5: key TaxonomyCode|TableCode, two-sided, absent = not applicable.
    // -----------------------------------------------------------------------------------------

    private void AddCellTable(SqliteConnection c, string taxonomy, string table, params (int IsShaded, string? Dps)[] cells)
    {
        var id = _nextId++;
        Exec(c, $"INSERT INTO mTaxonomy (TaxonomyID, TaxonomyCode) VALUES ({id}, '{taxonomy}')");
        Exec(c, $"INSERT INTO mTable (TableID, TableCode) VALUES ({id}, '{table}')");
        Exec(c, $"INSERT INTO mTaxonomyTable (TaxonomyID, TableID, AnnotatedTableID) VALUES ({id}, {id}, {id})");
        var n = 0;
        foreach (var (isShaded, dps) in cells)
        {
            var dpsSql = dps is null ? "NULL" : $"'{dps}'";
            Exec(c, $"INSERT INTO mTableCell (CellID, TableID, IsShaded, DPS) VALUES ({id * 100 + n++}, {id}, {isShaded}, {dpsSql})");
        }
    }

    private static (CheckResult Check, List<KnownExceptions.Outcome> Outcomes) RunIsig07(SqliteConnection c)
    {
        var sink = new List<KnownExceptions.Outcome>();
        var results = ConceptChecks.Run(c, ValidationSourceModel.Dpm2, sink).ToList();
        return (Assert.Single(results, r => r.Id == "I-SIG-07"), sink);
    }

    [Fact]
    public void ISig07_DeclaredTable_PresentAndViolating_IsApplied()
    {
        using var c = CreateAndOpen("isig07-applied.db");
        AddCellTable(c, "corep 3.5", Ao5Table, (0, "A(x)|B(y)"), (0, null), (1, null));

        var (check, outcomes) = RunIsig07(c);

        Assert.Equal(CheckStatus.Pass, check.Status);
        Assert.Equal(0, check.Failed);
        Assert.Equal(2, check.Examined); // the shaded cell is not examined
        var applied = Outcome(outcomes, $"corep 3.5|{Ao5Table}");
        Assert.Equal("AO-5", applied.Exception.Id);
        Assert.True(applied.AppliesToThisReference);
        Assert.Equal(1, applied.Matched);
        Assert.False(applied.Stale);
    }

    [Fact]
    public void ISig07_DeclaredTable_PresentButAllCellsHaveAMetric_IsStale_AndFails()
    {
        using var c = CreateAndOpen("isig07-stale.db");
        AddCellTable(c, "corep 3.5", Ao5Table, (0, "MET(eba_qCFB)|A(x)"), (0, "MET(eba_qCFB)"));

        var (check, outcomes) = RunIsig07(c);

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal(1, check.Failed);
        Assert.Contains(check.Samples, s => s.BusinessKey.Contains($"corep 3.5|{Ao5Table}", StringComparison.Ordinal));
        Assert.True(Outcome(outcomes, $"corep 3.5|{Ao5Table}").Stale);
    }

    [Fact]
    public void ISig07_DeclaredTable_AbsentFromTheFile_DoesNotApply_IsNotStale_AndDoesNotFail()
    {
        using var c = CreateAndOpen("isig07-absent.db");
        AddCellTable(c, "corep 4.0", "C_01.00", (0, "MET(eba_mi1)"));

        var (check, outcomes) = RunIsig07(c);

        Assert.Equal(CheckStatus.Pass, check.Status);
        Assert.Equal(0, check.Failed);
        var ao5 = Assert.Single(outcomes, o => o.Exception.Id == "AO-5");
        Assert.False(ao5.AppliesToThisReference);
        Assert.False(ao5.Stale);
    }

    /// <summary>Positive control: an undeclared table with cells without a metric fails, counted per cell.</summary>
    [Fact]
    public void ISig07_UndeclaredViolation_StillFails_PerCell()
    {
        using var c = CreateAndOpen("isig07-undeclared.db");
        AddCellTable(c, "corep 3.5", "C_99.00", (0, null), (0, "A(x)"), (0, "MET(eba_mi1)"));

        var (check, _) = RunIsig07(c);

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal(2, check.Failed);
        Assert.Equal(3, check.Examined);
        Assert.Contains(check.Samples, s => s.BusinessKey.StartsWith("corep 3.5|C_99.00", StringComparison.Ordinal));
    }

    /// <summary>The key includes the taxonomy: C_96.05.2 under 4.0 (where qCFB resolves) is NOT covered by the 3.5 exception.</summary>
    [Fact]
    public void ISig07_SameTableUnderAnotherTaxonomy_IsNotCovered()
    {
        using var c = CreateAndOpen("isig07-other-taxonomy.db");
        AddCellTable(c, "corep 4.0", Ao5Table, (0, null));

        var (check, outcomes) = RunIsig07(c);

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal(1, check.Failed);
        Assert.Contains(check.Samples, s => s.BusinessKey.StartsWith($"corep 4.0|{Ao5Table}", StringComparison.Ordinal));
        Assert.False(Assert.Single(outcomes, o => o.Exception.Id == "AO-5").AppliesToThisReference);
    }

    /// <summary>
    /// DPM 1.0 behaviour of I-SIG-07 is unchanged: the 4.1 output holds C_96.05.2 under "corep 3.5"
    /// with every non-shaded cell carrying a metric (measured on the real 4.1 output, where AO-5
    /// reported "no longer violates" and made --validate exit 3). A DPM 2.0 origin-anomaly entry
    /// must not turn a clean DPM 1.0 output into a stale-exception failure.
    /// </summary>
    [Fact]
    public void ISig07_Dpm1_TableOfAnAo5Key_WithEveryCellCarryingAMetric_DoesNotFail()
    {
        using var c = CreateAndOpen("isig07-dpm1-ao5.db");
        AddCellTable(c, "corep 3.5", Ao5Table, (0, "MET(eba_met:qCFB)|A(x)"), (0, "MET(eba_met:qCFB)|A(y)"));

        var sink = new List<KnownExceptions.Outcome>();
        var results = ConceptChecks.Run(c, ValidationSourceModel.Dpm1, sink).ToList();
        var check = Assert.Single(results, r => r.Id == "I-SIG-07");

        Assert.Equal(CheckStatus.Pass, check.Status);
        Assert.Equal(0, check.Failed);
        Assert.DoesNotContain(sink, o => o.Stale);
    }
}
