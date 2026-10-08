using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Tests.Dpm2;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// Proves that a violation OUTSIDE the declared entries of the plane C known-divergence census
/// (<c>plane-c-known-divergences-4.2.tsv</c>) makes <c>C-DPS-01</c> FAIL. The mechanism
/// (<c>KnownExceptions.ApplyOriginAnomalyExceptions</c>) is generic and pre-existing -- what this
/// file adds is the proof that, for <c>C-DPS-01</c> specifically, an UNLISTED violation is still a
/// real failure and is not absorbed by the census by accident (the only assertion that turns the
/// census into a GATE instead of decoration).
///
/// Two deliberately complementary levels:
/// <list type="bullet">
/// <item><b>End to end</b> (<see cref="CDps01_WithAnAlteredRealSignature_ProducesAnUnlistedViolation_AndFails"/>):
/// COPIES of the REAL DPM 2.0 4.2 corpus (<see cref="Dpm20SkeletonFixture"/>, the same output that
/// <c>PlaneCRealDataTests</c> already proves reproduces the declared entries and no others) -- a
/// real signature is altered by ADDING a new synthetic table and sheet (an existing row is never
/// touched: the declared entries keep reproducing exactly the same, and the only measurable change
/// is the new violation). Real production path, <c>Validator.Run</c> included. Copies, because
/// <see cref="Dpm20SkeletonFixture"/> and <c>PlaneCLayoutRepositoryHolder</c> are SHARED by the
/// rest of the suite -- mutating them in place would break other tests.</item>
/// <item><b>Focused on the mechanism</b> (<see cref="ApplyOriginAnomalyExceptions_WithAKeyFromTheCensusAndAnUnlistedOne_OnlyTheUnlistedOneStaysUnresolved"/>):
/// <c>KnownExceptions.ApplyOriginAnomalyExceptions</c> in isolation, with a REAL key of the census
/// (taken from the loaded census, never copied by hand) next to an invented one -- proves that the
/// filter discriminates between the two, not that "anything" passes or fails.</item>
/// </list>
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class PlaneCCensusGateTests(Dpm20SkeletonFixture fixture) : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), $"EbaDpm.KnownDivergenceGateTests_{Environment.ProcessId}_{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort.
        }
    }

    /// <summary>
    /// Positive control: on COPIES of the real DPM 2.0 4.2 corpus and of the real 4.2 layout
    /// repository, a synthetic table "ZZ_TEST.01" is ADDED (an existing row is never altered) whose
    /// real signature is <c>MET(qAA)|DIM(m1)</c> while the corresponding layout sheet asks for
    /// <c>DIM(m2)</c> -- <c>Contradicts</c> -- with business key
    /// <c>"ZZ_TEST.01\tMET(qAA)|DIM(m2)"</c>, which CANNOT match any of the real entries of
    /// <c>plane-c-known-divergences-4.2.tsv</c> (all of them from <c>F_02.00.dp</c> /
    /// <c>F_18.00.a.dp</c>). Since the REST of the real corpus stays intact, the declared entries
    /// keep reproducing exactly the same (they do not "shrink": <c>PlaneCRealDataTests</c> already
    /// proves that on the same <see cref="Dpm20SkeletonFixture"/>) and <c>Failed</c> measures
    /// EXACTLY the new violation -- <c>Status=fail</c>, <c>Failed=1</c>, the sample names that key.
    /// </summary>
    [DataFact]
    public void CDps01_WithAnAlteredRealSignature_ProducesAnUnlistedViolation_AndFails()
    {
        Directory.CreateDirectory(_tempDirectory);
        var generatedPath = Path.Combine(_tempDirectory, "generated.db");
        var layoutsPath = Path.Combine(_tempDirectory, "layouts.db");

        SqliteConnection.ClearAllPools();
        File.Copy(fixture.ValidatedDatabasePath, generatedPath);
        File.Copy(PlaneCLayoutRepositoryHolder.LayoutsDatabasePath, layoutsPath);

        const string extraTableCode = "ZZ_TEST.01";
        InsertExtraTableAndCell(generatedPath, tableCode: extraTableCode, frameworkCode: "COREP", release: "4.2", dps: "MET(qAA)|DIM(m1)");
        InsertExtraComparerLayoutSheet(layoutsPath, tableCode: extraTableCode, frameworkCode: "COREP", release: "4.2", metMember: "qAA", dimMember: "m2");

        SqliteConnection.ClearAllPools();
        var result = Validator.Run(generatedPath, referencePath: null, layoutsPath);

        var cDps01 = Assert.Single(result.Report.Checks, c => c.Id == "C-DPS-01");

        // PlaneCSignature.Canonicalize sorts the terms alphabetically -- "DIM(...)" before
        // "MET(...)" -- regardless of the order in which the layout terms were inserted.
        var expectedKey = $"{extraTableCode}\tDIM(m2)|MET(qAA)";

        // Built-in negative control: the injected key is NOT any of the current census entries --
        // if it were, this test would not prove what it claims to prove.
        Assert.DoesNotContain(KnownExceptions.All.Where(e => e.Id == "DD-23").Select(e => e.BusinessKey), k => k == expectedKey);

        Assert.Equal("fail", cDps01.Status);
        Assert.Equal(1, cDps01.Failed);
        Assert.Contains(cDps01.Samples, s => s.BusinessKey == expectedKey);
    }

    /// <summary>
    /// Focus on the mechanism, without a database: a REAL key taken from the already loaded census
    /// (<see cref="KnownExceptions.All"/>, never copied by hand -- if the TSV changes, this test
    /// moves with it) next to an invented key that is in no list. After
    /// <c>ApplyOriginAnomalyExceptions("C-DPS-01", ...)</c>, the census one is ABSORBED (not in
    /// <c>Unresolved</c>) and the invented one stays UNRESOLVED -- exactly the half that
    /// <c>PlaneCChecks.BuildCDps01</c> uses to compute <c>Failed</c>. Complements the test above:
    /// it shows that the filter DISCRIMINATES between the two keys, not that the mechanism "always
    /// let everything through" or "always made it fail" for some other reason.
    /// </summary>
    [Fact]
    public void ApplyOriginAnomalyExceptions_WithAKeyFromTheCensusAndAnUnlistedOne_OnlyTheUnlistedOneStaysUnresolved()
    {
        var censusKey = KnownExceptions.All.First(e => e.Id == "DD-23").BusinessKey;
        const string unlistedKey = "ZZ_TEST.01\tMET(qAA)|DIM(m2)";

        var (unresolved, outcomes) = KnownExceptions.ApplyOriginAnomalyExceptions(
            "C-DPS-01", new HashSet<string>(StringComparer.Ordinal) { censusKey, unlistedKey }, ValidationPlane.C);

        Assert.Contains(unlistedKey, unresolved);
        Assert.DoesNotContain(censusKey, unresolved);

        var censusOutcome = Assert.Single(outcomes, o => o.Exception.BusinessKey == censusKey);
        Assert.Equal(1, censusOutcome.Matched); // it still violates, and the census recognizes it as its own.
        Assert.False(censusOutcome.Stale, $"The real census key '{censusKey}' came out STALE -- unexpected within this synthetic set.");
    }

    /// <summary>Adds (never alters) a new table with a taxonomy link and a cell with a signature
    /// -- the half of the output that <c>PlaneCComparer</c> needs. Sentinel IDs (&gt;=900,000,000)
    /// so as not to collide with any real ID of the DPM 2.0 4.2 conversion.</summary>
    private static void InsertExtraTableAndCell(
        string generatedPath, string tableCode, string frameworkCode, string release, string dps)
    {
        using var connection = OpenReadWrite(generatedPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            INSERT INTO "mTable" ("TableID", "TableCode") VALUES (900000001, '{tableCode}');
            INSERT INTO "mTableCell" ("CellID", "TableID", "DPS") VALUES (900000001, 900000001, '{dps}');
            INSERT INTO "mReportingFramework" ("FrameworkID", "FrameworkCode") VALUES (900000001, '{frameworkCode}');
            INSERT INTO "mTaxonomy" ("TaxonomyID", "TaxonomyCode", "FrameworkID")
                VALUES (900000001, '{frameworkCode.ToLowerInvariant()} {release}', 900000001);
            INSERT INTO "mTaxonomyTable" ("TaxonomyID", "TableID", "AnnotatedTableID") VALUES (900000001, 900000001, 900000001);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds (never alters) a sheet to the layout repository -- a single <c>MET(metMember)</c> term
    /// and a <c>DIM(dimMember)</c> term -- same pattern as
    /// <c>PlaneCComparerTests.BuildMatchingLayoutSheet</c>, with sentinel IDs.
    ///
    /// The real repository (<c>LayoutRepositorySchema</c>) requires NOT NULL columns that the
    /// synthetic fixture of <c>PlaneCComparerTests</c> / <c>PlaneCSyntheticDatabase</c> does not
    /// model (<c>LayoutFile.Path</c>/<c>Sha256</c>/<c>ModuleCode</c>, <c>LayoutSheet.SheetName</c>,
    /// <c>LayoutDeclaration.Region</c>/<c>CellRef</c>, <c>LayoutValue.CellRef</c>,
    /// <c>LayoutCell.CellRef</c>/<c>ColumnOrdinateId</c>) -- they are filled with meaningless
    /// SENTINEL values (never read: <c>PlaneCLayoutReader.Read</c> does not query them), only to
    /// satisfy the schema of the real copy.
    /// </summary>
    private static void InsertExtraComparerLayoutSheet(
        string layoutsPath, string tableCode, string frameworkCode, string release, string metMember, string dimMember)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = layoutsPath }.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            INSERT INTO "LayoutFile" ("FileId","Path","Sha256","FrameworkCode","ModuleCode","ReleaseLabel")
                VALUES (900000001, 'ZZ_TEST_FIXTURE', 'ZZ_TEST_FIXTURE', '{frameworkCode}', 'ZZ_TEST_FIXTURE', '{release}');
            INSERT INTO "LayoutSheet" ("SheetId","FileId","SheetName","TableCode")
                VALUES (900000001, 900000001, '{tableCode}', '{tableCode}');
            INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","Region","CellRef","DimensionCode","DomainCode","IsKey")
                VALUES (900000001, 900000001, 'ZZ_TEST_FIXTURE', 'A1', NULL, 'MET', 0);
            INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode","CellRef")
                VALUES (900000001, 900000001, 900000001, 900000001, '{metMember}', 'A1');
            INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","Region","CellRef","DimensionCode","DomainCode","IsKey")
                VALUES (900000002, 900000001, 'ZZ_TEST_FIXTURE', 'A2', 'DIM', NULL, 0);
            INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode","CellRef")
                VALUES (900000002, 900000001, 900000002, 900000001, '{dimMember}', 'A2');
            INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","CellRef","IsShaded")
                VALUES (900000001, 900000001, NULL, 900000001, 'B1', 0);
            """;
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection OpenReadWrite(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();

        // Same criterion as PlaneCEquGuardTests.OpenReadWrite: no foreign_keys, to insert synthetic
        // rows without fabricating ALL the rows that the declared FKs would require.
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }
}
