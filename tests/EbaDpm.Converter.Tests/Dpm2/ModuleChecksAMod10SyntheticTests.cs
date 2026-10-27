using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Core.Validation.Checks;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// A-MOD-10 (plane A, critical, DPM 2.0 only; issue 66): every <c>mTaxonomy</c> has at least one
/// <c>mModule</c>, and each module's <c>XBRLSchemaRef</c> segment <c>&lt;framework&gt;/&lt;release&gt;</c>
/// equals its taxonomy's <c>TaxonomyCode</c> with the space replaced by <c>/</c>. White-box tests
/// on minimal SQLite databases (the empty 45-table schema plus rows built by hand): each failing
/// case is the POSITIVE CONTROL of the zero that the correct database gives.
/// </summary>
public sealed class ModuleChecksAMod10SyntheticTests : IDisposable
{
    private const string RefPrefix = "http://www.eba.europa.eu/eu/fr/xbrl/crr/fws/";

    private readonly string _dir;

    public ModuleChecksAMod10SyntheticTests()
    {
        _dir = Path.Join(Path.GetTempPath(), $"EbaDpm.AMod10_{Environment.ProcessId}_{Guid.NewGuid():N}");
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
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();
        SqlHelpers.Execute(connection, "PRAGMA foreign_keys = OFF");
        return connection;
    }

    private static void Exec(SqliteConnection c, string sql) => SqlHelpers.Execute(c, sql);

    /// <summary>A consistent DPM 2.0 world: corep 4.2 (one module), corep 4.0 (one module) and finrep 4.2.1 (one module).</summary>
    private SqliteConnection ConsistentDatabase(string fileName)
    {
        var c = CreateAndOpen(fileName);
        Exec(c, "INSERT INTO mTaxonomy (TaxonomyID, TaxonomyCode) VALUES (1, 'corep 4.2'), (2, 'corep 4.0'), (3, 'finrep 4.2.1')");
        Exec(c, $"INSERT INTO mModule (ModuleID, TaxonomyID, ModuleCode, XBRLSchemaRef) VALUES "
            + $"(1, 1, 'COREP_OF', '{RefPrefix}corep/4.2/mod/corep_of.xsd'), "
            + $"(2, 2, 'COREP_LR', '{RefPrefix}corep/4.0/mod/corep_lr.xsd'), "
            + $"(3, 3, 'FINREP9DP', '{RefPrefix}finrep/4.2.1/mod/finrep9dp.xsd')");
        return c;
    }

    private static CheckResult AMod10(SqliteConnection c, ValidationSourceModel model = ValidationSourceModel.Dpm2)
        => Assert.Single(ModuleChecks.Run(c, model), r => r.Id == "A-MOD-10");

    [Fact]
    public void ACorrectDatabase_Passes_AndExaminesTaxonomiesAndModules()
    {
        using var c = ConsistentDatabase("correct.db");

        var result = AMod10(c);

        Assert.Equal(CheckStatus.Pass, result.Status);
        Assert.Equal(0, result.Failed);
        Assert.Equal(6, result.Examined); // 3 taxonomies + 3 modules: the zero is over a real universe
        Assert.Equal(ValidationLayer.Critical, result.Layer);
        Assert.Equal(ValidationPlane.A, result.Plane);
    }

    [Fact]
    public void ATaxonomyWithoutAModule_Fails_AndNamesIt()
    {
        using var c = ConsistentDatabase("no-module.db");
        Exec(c, "INSERT INTO mTaxonomy (TaxonomyID, TaxonomyCode) VALUES (4, 'esg 4.1')");

        var result = AMod10(c);

        Assert.Equal(CheckStatus.Fail, result.Status);
        Assert.Equal(1, result.Failed);
        Assert.Contains(result.Samples, s => s.BusinessKey.StartsWith("esg 4.1", StringComparison.Ordinal)
            && s.BusinessKey.Contains("without any module", StringComparison.Ordinal));
    }

    [Fact]
    public void ASchemaRefOnAnotherRelease_Fails_AndNamesTheModule()
    {
        using var c = ConsistentDatabase("other-release.db");
        // COREP_LR belongs to corep 4.0 but its schema reference says 4.2 (the old "cutoff for every module" shape).
        Exec(c, $"UPDATE mModule SET XBRLSchemaRef = '{RefPrefix}corep/4.2/mod/corep_lr.xsd' WHERE ModuleCode = 'COREP_LR'");

        var result = AMod10(c);

        Assert.Equal(CheckStatus.Fail, result.Status);
        Assert.Equal(1, result.Failed);
        Assert.Contains(result.Samples, s => s.BusinessKey.StartsWith("COREP_LR", StringComparison.Ordinal));
    }

    [Fact]
    public void ASchemaRefOnAnotherFramework_Fails()
    {
        using var c = ConsistentDatabase("other-framework.db");
        Exec(c, $"UPDATE mModule SET XBRLSchemaRef = '{RefPrefix}esg/4.2.1/mod/finrep9dp.xsd' WHERE ModuleCode = 'FINREP9DP'");

        var result = AMod10(c);

        Assert.Equal(CheckStatus.Fail, result.Status);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public void AModuleWhoseTaxonomyDoesNotExist_Fails()
    {
        using var c = ConsistentDatabase("dangling-taxonomy.db");
        Exec(c, $"INSERT INTO mModule (ModuleID, TaxonomyID, ModuleCode, XBRLSchemaRef) VALUES (9, 99, 'LOST', '{RefPrefix}corep/4.2/mod/lost.xsd')");

        var result = AMod10(c);

        Assert.Equal(CheckStatus.Fail, result.Status);
        Assert.Contains(result.Samples, s => s.BusinessKey.StartsWith("LOST", StringComparison.Ordinal));
    }

    [Fact]
    public void OnADpm10Output_TheCheckIsNotRun_EvenWithATaxonomyWithoutModule()
    {
        // DPM 1.0 keeps taxonomies without modules (A-MOD-09 is an anti-invariant there): the same
        // database that fails under DPM 2.0 must not even produce the result under DPM 1.0.
        using var c = ConsistentDatabase("dpm1.db");
        Exec(c, "INSERT INTO mTaxonomy (TaxonomyID, TaxonomyCode) VALUES (4, 'esg 4.1')");

        Assert.Equal(CheckStatus.Fail, AMod10(c, ValidationSourceModel.Dpm2).Status); // positive control
        Assert.DoesNotContain(ModuleChecks.Run(c, ValidationSourceModel.Dpm1), r => r.Id == "A-MOD-10");
    }
}
