using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Mapping.Dpm20;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Synthetic tests of <see cref="Dpm20TaxonomyDeriver"/> (issue 66, DPM 2.0 only): a module
/// version hangs from the taxonomy (framework, <c>ModuleVersion.StartReleaseID</c>), not from the
/// cutoff release; only the taxonomies of emitted module versions (current, not DORA, not a
/// document module) exist; the tables of a taxonomy are the non-abstract compositions of its
/// module versions plus the abstract closure. No Access, no data files.
///
/// The world: cutoff release 3 (<c>4.2</c>); COREP has two modules, one that starts in 4.0 and one
/// that starts in 4.2; FINREP one module starting in 4.2; SOLO one module starting in 4.0 only;
/// DORA and a PILLAR3 document module start in 4.2; EMPTY has a module but no module version.
/// </summary>
public sealed class Dpm20TaxonomyDeriverSyntheticTests
{
    private const int CutoffReleaseId = 3;

    private static readonly Dpm20ReleaseRow[] Releases =
    [
        new(1, "4.0", null, null, null, false),
        new(2, "4.1", null, null, null, false),
        new(3, "4.2", null, null, null, true),
        new(4, "4.2.1", null, null, null, false),
    ];

    private static readonly Dpm20FrameworkRow[] Frameworks =
    [
        new(1, "COREP", "Corep"),
        new(2, "FINREP", "Finrep"),
        new(3, "DORA", "Dora"),
        new(4, "PILLAR3", "Pillar 3"),
        new(5, "SOLO", "Solo"),
        new(6, "EMPTY", "Empty"),
    ];

    private static Dpm20ModuleRow[] Modules(bool pillar3IsDocumentModule = true) =>
    [
        new(10, 1, false), // COREP, version starts in 4.0
        new(11, 1, false), // COREP, version starts in 4.2
        new(12, 2, false), // FINREP
        new(13, 3, false), // DORA
        new(14, 4, pillar3IsDocumentModule), // PILLAR3 document module
        new(15, 5, false), // SOLO
        new(16, 6, false), // EMPTY: no module version at all
    ];

    private static readonly Dpm20ModuleVersionRow[] ModuleVersions =
    [
        new(100, 10, "corep_old", "Corep old", 1, null),
        new(101, 11, "corep_new", "Corep new", 3, null),
        new(102, 12, "finrep_new", "Finrep new", 3, null),
        new(103, 13, "dora_ict", "Dora", 3, null),
        new(104, 14, "p3_docs", "Documents", 3, null),
        new(105, 15, "solo_old", "Solo old", 1, null),
    ];

    private static Dpm20TableVersionRow Table(int vid, string code, int tableId, int? abstractTableId = null, bool isAbstract = false)
        => new(vid, code, code, tableId, abstractTableId, 1, null, isAbstract, null);

    private static readonly Dpm20TableVersionRow[] TableVersions =
    [
        Table(200, "C_01.00", 1), // shared by corep_old and corep_new
        Table(201, "C_02.00", 2), // only corep_old
        Table(202, "C_03.00", 3), // only corep_new
        Table(203, "F_01.00", 4), // finrep_new
        Table(204, "D_01.00", 5), // only DORA
        Table(205, "P_DOC.00", 6), // only the document module
        Table(206, "C_09.00", 50, isAbstract: true), // abstract, declared by corep_new
        Table(207, "C_09.00.a", 51, abstractTableId: 50),
        Table(208, "C_09.00.b", 52, abstractTableId: 50),
        Table(209, "S_01.00", 7), // solo_old
    ];

    private static readonly Dpm20ModuleVersionCompositionRow[] Compositions =
    [
        new(100, 200), new(101, 200), // shared table
        new(100, 201),
        new(101, 202),
        new(101, 206), // abstract only: the closure adds its two concrete children
        new(102, 203),
        new(103, 204),
        new(104, 205),
        new(105, 209),
    ];

    private static Dpm20TaxonomyDerivationResult Derive(
        Dpm20ModuleRow[]? modules = null,
        Dpm20ModuleVersionRow[]? moduleVersions = null,
        Dpm20ModuleVersionCompositionRow[]? compositions = null)
        => Dpm20TaxonomyDeriver.Derive(
            Releases,
            Frameworks,
            modules ?? Modules(),
            moduleVersions ?? ModuleVersions,
            compositions ?? Compositions,
            TableVersions,
            CutoffReleaseId);

    private static IReadOnlyList<string> Codes(Dpm20TaxonomyDerivationResult result)
        => result.Taxonomies.Select(t => t.TaxonomyCode).OrderBy(c => c, StringComparer.Ordinal).ToList();

    private static List<string> TableCodes(Dpm20TaxonomyDerivationResult result, string taxonomyCode)
    {
        var taxonomyId = result.Taxonomies.Single(t => t.TaxonomyCode == taxonomyCode).TaxonomyId;
        var codeByVId = TableVersions.ToDictionary(t => t.TableVId, t => t.Code);
        return result.TableVIdsByTaxonomyId[taxonomyId].Select(v => codeByVId[v]).OrderBy(c => c, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void ModuleVersionStartingBeforeTheCutoff_LandsInItsStartReleaseTaxonomy_NotInTheCutoffOne()
    {
        var result = Derive();

        Assert.Contains("corep 4.0", Codes(result));
        Assert.Contains("solo 4.0", Codes(result));
        // The cutoff release (4.2) is NOT a taxonomy of SOLO: none of its module versions starts there.
        Assert.DoesNotContain("solo 4.2", Codes(result));
        Assert.Equal(["S_01.00"], TableCodes(result, "solo 4.0"));
    }

    [Fact]
    public void TwoModulesOfOneFramework_WithDifferentStartReleases_ProduceTwoTaxonomies()
    {
        var result = Derive();

        Assert.Equal(["corep 4.0", "corep 4.2"], Codes(result).Where(c => c.StartsWith("corep ", StringComparison.Ordinal)));
        Assert.Equal(["C_01.00", "C_02.00"], TableCodes(result, "corep 4.0"));
        // corep 4.2 carries only the tables of the module version that starts in 4.2.
        Assert.DoesNotContain("C_02.00", TableCodes(result, "corep 4.2"));
        Assert.Contains("C_03.00", TableCodes(result, "corep 4.2"));
    }

    [Fact]
    public void ATableSharedByModuleVersionsOfTwoTaxonomies_IsInBoth()
    {
        var result = Derive();

        Assert.Contains("C_01.00", TableCodes(result, "corep 4.0"));
        Assert.Contains("C_01.00", TableCodes(result, "corep 4.2"));
        // Same table version, two taxonomies: the pair count is 2 for it.
        var pairs = result.TableVIdsByTaxonomyId.Values.SelectMany(v => v).Count(v => v == 200);
        Assert.Equal(2, pairs);
    }

    [Fact]
    public void DocumentModulesAndDora_ProduceNoTaxonomy_AndTheirTablesAppearNowhere()
    {
        var result = Derive();

        Assert.DoesNotContain(Codes(result), c => c.StartsWith("dora ", StringComparison.Ordinal));
        Assert.DoesNotContain(Codes(result), c => c.StartsWith("pillar3 ", StringComparison.Ordinal));
        var allTables = result.TableVIdsByTaxonomyId.Values.SelectMany(v => v).ToHashSet();
        Assert.DoesNotContain(204, allTables); // DORA only
        Assert.DoesNotContain(205, allTables); // document module only
    }

    [Fact]
    public void PositiveControl_TheSameWorld_WithPillar3NotADocumentModule_DoesProduceItsTaxonomy()
    {
        // Proves the exclusion above is what keeps "pillar3 4.2" out: remove the flag and it appears.
        var result = Derive(modules: Modules(pillar3IsDocumentModule: false));

        Assert.Contains("pillar3 4.2", Codes(result));
        Assert.Equal(["P_DOC.00"], TableCodes(result, "pillar3 4.2"));
    }

    [Fact]
    public void NoTaxonomyWithoutAModule()
    {
        var result = Derive();

        // EMPTY has a [Module] row but no module version: no taxonomy.
        Assert.DoesNotContain(Codes(result), c => c.StartsWith("empty ", StringComparison.Ordinal));
        // The taxonomies are exactly the distinct (framework, start release) of the emitted module versions.
        Assert.Equal(["corep 4.0", "corep 4.2", "finrep 4.2", "solo 4.0"], Codes(result));
    }

    [Fact]
    public void ATaxonomyWhoseModuleVersionComposesNoTable_StillExists_WithoutTables()
    {
        var result = Derive(compositions: [.. Compositions.Where(c => c.ModuleVId != 102)]);

        Assert.Contains("finrep 4.2", Codes(result));
        Assert.Empty(TableCodes(result, "finrep 4.2"));
        Assert.Equal(0, result.TableCounts.Single(c => c.TaxonomyId == result.Taxonomies.Single(t => t.TaxonomyCode == "finrep 4.2").TaxonomyId).TableCount);
    }

    [Fact]
    public void AbstractClosure_AddsTheConcreteChildren_NotTheAbstractTable_AndOnlyInTheTaxonomyThatDeclaresIt()
    {
        var result = Derive();

        var corepNew = TableCodes(result, "corep 4.2");
        Assert.Contains("C_09.00.a", corepNew);
        Assert.Contains("C_09.00.b", corepNew);
        Assert.DoesNotContain("C_09.00", corepNew);
        Assert.DoesNotContain("C_09.00.a", TableCodes(result, "corep 4.0"));
        Assert.Equal(["C_01.00", "C_03.00", "C_09.00.a", "C_09.00.b"], corepNew);
    }

    [Fact]
    public void TaxonomyRows_CarryFrameworkReleaseAndKey_AndIdsAreDeterministicByCode()
    {
        var first = Derive();
        var second = Derive();

        Assert.Equal(
            first.Taxonomies.Select(t => (t.TaxonomyCode, t.TaxonomyId)),
            second.Taxonomies.Select(t => (t.TaxonomyCode, t.TaxonomyId)));
        Assert.Equal([1, 2, 3, 4], first.Taxonomies.Select(t => t.TaxonomyId));

        var corepOld = first.Taxonomies.Single(t => t.TaxonomyCode == "corep 4.0");
        Assert.Equal("4.0", corepOld.DpmPackageCode);
        Assert.Equal("corep", corepOld.TechnicalStandard);
        Assert.Equal(1, corepOld.FrameworkId);
        Assert.Equal("corep/4.0", first.TaxonomyKeyByTaxonomyId[corepOld.TaxonomyId]);
    }
}
