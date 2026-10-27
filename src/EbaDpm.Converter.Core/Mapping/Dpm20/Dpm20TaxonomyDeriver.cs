using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Access.Dpm20;

namespace EbaDpm.Converter.Core.Mapping.Dpm20;

/// <summary>
/// Result of <see cref="Dpm20TaxonomyDeriver.Derive"/>: the derived taxonomies plus the auxiliary
/// information needed later for <c>mTaxonomyTable</c>, which comes out for free here because it is
/// already computed to produce <see cref="Taxonomies"/>.
/// </summary>
/// <param name="Taxonomies">
/// The derived taxonomies, in the <see cref="AccessTaxonomyRow"/> shape deliberately reused from
/// the DPM 1.0 pipeline so that <c>TaxonomySelector</c> and <c>TaxonomySelectionRequest</c> work
/// unchanged. One row per distinct (framework, <c>ModuleVersion.StartReleaseID</c>) of the emitted
/// module versions.
/// </param>
/// <param name="TableCounts">Count of table versions per taxonomy, in the same shape <c>--list-taxonomies</c> uses today for DPM 1.0.</param>
/// <param name="TaxonomyKeyByTaxonomyId">The <c>TaxonomyKey</c> of each derived taxonomy: <c>lowercase-framework/release</c>, e.g. <c>corep/4.2</c>, with the release the module versions of the taxonomy start in.</param>
/// <param name="TableVIdsByTaxonomyId">The set of <c>TableVID</c> of each derived taxonomy. Not consumed when listing taxonomies; needed to populate <c>mTaxonomyTable</c> (exposing it now is free).</param>
public sealed record Dpm20TaxonomyDerivationResult(
    IReadOnlyList<AccessTaxonomyRow> Taxonomies,
    IReadOnlyList<AccessTaxonomyTableCount> TableCounts,
    IReadOnlyDictionary<int, string> TaxonomyKeyByTaxonomyId,
    IReadOnlyDictionary<int, IReadOnlyList<int>> TableVIdsByTaxonomyId);

/// <summary>
/// Derives the taxonomy in DPM 2.0: there is no <c>Taxonomy</c> table in the source, so it is
/// computed from <c>Framework</c>, <c>[Release]</c>, <c>[Module]</c>, <c>ModuleVersion</c>,
/// <c>ModuleVersionComposition</c> and <c>TableVersion</c> ⋈ <c>[Table]</c>.
///
/// Rules:
///
/// 1. **Emitted module versions**: the current ones at the cutoff release (already filtered in
///    <see cref="Dpm20AccessReader.ReadModuleVersions"/>), excluding the <c>DORA</c> framework and
///    <c>Module.isDocumentModule = True</c> — the same selection <see cref="Dpm20ModuleLoader"/>
///    emits.
/// 2. **Taxonomy of a module version** = (framework of its module, <c>ModuleVersion.StartReleaseID</c>):
///    the EBA taxonomy that publishes that module version, NOT the cutoff release.
///    <c>TaxonomyCode</c> = lowercase framework code + space + release code; <c>DpmPackageCode</c>
///    = release code. The taxonomies are exactly the distinct pairs of the emitted module versions
///    (no taxonomy without a module). Measured 2026-10-27: the 4.2.1 database gives 19 taxonomies
///    for 50 modules (only <c>FINREP9DP</c> on 4.2.1); the 4.3 database gives 21 taxonomies for 54
///    modules (49 on 4.2, <c>FINREP9DP</c> on 4.2.1, <c>AML_RA</c> and the <c>TCB_*</c> modules on 4.3).
/// 3. **Tables of a taxonomy**: the current, non-abstract table versions in
///    <c>ModuleVersionComposition</c> of the taxonomy's emitted module versions. A table in modules
///    of two taxonomies belongs to both. Tables that only DORA modules or document modules carry
///    never appear.
/// 3b. <b>Abstract closure</b>: if the composition row declares an ABSTRACT table, its CURRENT
///    concrete children are added as well (<c>TableVersion.AbstractTableID</c> pointing to that
///    abstract table), even if they have no row of their own in <c>ModuleVersionComposition</c>
///    for that module. It is a rule of the Access database, backed by the invariant "a module
///    that declares an abstract table declares all its current children" (394/395 in 4.2, 408/408
///    in 4.3; the only failure in the corpus is <c>IF_CLASS2 → C_34.02.b</c>, which Access itself
///    restores in 4.3). It is NOT propagated through <c>TableGroup</c> (that route over-produced
///    26 extra pairs).
///
/// <c>TaxonomyId</c> is synthetic and deterministic: it is ordered by <c>TaxonomyCode</c> and
/// numbered from 1 — there is no source id to reuse, because the entity does not exist there.
/// </summary>
public static class Dpm20TaxonomyDeriver
{
    public static Dpm20TaxonomyDerivationResult Derive(
        IReadOnlyList<Dpm20ReleaseRow> releases,
        IReadOnlyList<Dpm20FrameworkRow> frameworks,
        IReadOnlyList<Dpm20ModuleRow> modules,
        IReadOnlyList<Dpm20ModuleVersionRow> currentModuleVersions,
        IReadOnlyList<Dpm20ModuleVersionCompositionRow> moduleVersionCompositions,
        IReadOnlyList<Dpm20TableVersionRow> currentTableVersions,
        int cutoffReleaseId)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(frameworks);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(currentModuleVersions);
        ArgumentNullException.ThrowIfNull(moduleVersionCompositions);
        ArgumentNullException.ThrowIfNull(currentTableVersions);

        var releaseCodeById = releases.ToDictionary(r => r.ReleaseId, r => r.Code);
        if (!releaseCodeById.ContainsKey(cutoffReleaseId))
        {
            throw new InvalidOperationException(
                $"The cutoff release (ReleaseID={cutoffReleaseId}) is not in the provided release list.");
        }

        var frameworkById = frameworks.ToDictionary(f => f.FrameworkId);
        var doraFrameworkId = frameworks
            .Where(f => string.Equals(f.Code, "DORA", StringComparison.OrdinalIgnoreCase))
            .Select(f => (int?)f.FrameworkId)
            .FirstOrDefault();

        var moduleById = modules.ToDictionary(m => m.ModuleId);
        var tableVersionByTableVId = currentTableVersions.ToDictionary(tv => tv.TableVId);

        // Index for the abstract closure (rule 3b): abstract TableID -> TableVID of its CURRENT
        // concrete children (TableVersion.AbstractTableID -> that abstract table). Built over
        // currentTableVersions, which is already filtered by the cutoff release, so every child
        // here is current by construction.
        var concreteChildrenTableVIdsByAbstractTableId = new Dictionary<int, List<int>>();
        foreach (var tv in currentTableVersions)
        {
            if (tv.IsAbstract || tv.AbstractTableId is not { } parentTableId)
            {
                continue;
            }

            if (!concreteChildrenTableVIdsByAbstractTableId.TryGetValue(parentTableId, out var list))
            {
                list = [];
                concreteChildrenTableVIdsByAbstractTableId[parentTableId] = list;
            }

            list.Add(tv.TableVId);
        }

        // Step 1: emitted module versions (rules 1 and 2) and their taxonomy. The taxonomy exists
        // as soon as one emitted module version hangs from it, with or without tables.
        var tableVIdsByTaxonomyCode = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var frameworkIdByTaxonomyCode = new Dictionary<string, int>(StringComparer.Ordinal);
        var releaseCodeByTaxonomyCode = new Dictionary<string, string>(StringComparer.Ordinal);
        var taxonomyCodeByModuleVId = new Dictionary<int, string>();

        foreach (var moduleVersion in currentModuleVersions)
        {
            if (!moduleById.TryGetValue(moduleVersion.ModuleId, out var module))
            {
                continue; // reported as unresolved by Dpm20ModuleLoader.
            }

            if (module.IsDocumentModule || !frameworkById.TryGetValue(module.FrameworkId, out var framework))
            {
                continue;
            }

            if (doraFrameworkId is { } doraId && framework.FrameworkId == doraId)
            {
                continue;
            }

            if (!releaseCodeById.TryGetValue(moduleVersion.StartReleaseId, out var startReleaseCode))
            {
                throw new InvalidOperationException(
                    $"ModuleVersion.ModuleVID={moduleVersion.ModuleVId} has StartReleaseID={moduleVersion.StartReleaseId} "
                    + "with no matching row in [Release].");
            }

            var taxonomyCode = $"{framework.Code.ToLowerInvariant()} {startReleaseCode}";
            if (!tableVIdsByTaxonomyCode.ContainsKey(taxonomyCode))
            {
                tableVIdsByTaxonomyCode[taxonomyCode] = [];
                frameworkIdByTaxonomyCode[taxonomyCode] = framework.FrameworkId;
                releaseCodeByTaxonomyCode[taxonomyCode] = startReleaseCode;
            }

            taxonomyCodeByModuleVId[moduleVersion.ModuleVId] = taxonomyCode;
        }

        // Step 2: tables of each taxonomy (rule 3), closed with the abstract closure (rule 3b).
        foreach (var composition in moduleVersionCompositions)
        {
            if (!taxonomyCodeByModuleVId.TryGetValue(composition.ModuleVId, out var taxonomyCode))
            {
                continue; // ModuleVersion not emitted (not current, DORA or document module).
            }

            if (!tableVersionByTableVId.TryGetValue(composition.TableVId, out var tableVersion))
            {
                continue; // TableVersion not current at the cutoff release.
            }

            var set = tableVIdsByTaxonomyCode[taxonomyCode];

            if (!tableVersion.IsAbstract)
            {
                set.Add(composition.TableVId);
            }
            else if (concreteChildrenTableVIdsByAbstractTableId.TryGetValue(tableVersion.TableId, out var children))
            {
                // Rule 3b: the row declares an ABSTRACT table -> close it with its current concrete
                // children, even if the composition does not carry their explicit row (case
                // IF_CLASS2 -> C_34.02, abstract; children C_34.02.a declared and C_34.02.b not).
                foreach (var childTableVId in children)
                {
                    set.Add(childTableVId);
                }
            }
        }

        // Step 4: synthetic, deterministic TaxonomyId — ordered by TaxonomyCode, numbered from 1.
        var orderedCodes = tableVIdsByTaxonomyCode.Keys
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        var taxonomies = new List<AccessTaxonomyRow>(orderedCodes.Count);
        var tableCounts = new List<AccessTaxonomyTableCount>(orderedCodes.Count);
        var taxonomyKeyByTaxonomyId = new Dictionary<int, string>();
        var tableVIdsByTaxonomyId = new Dictionary<int, IReadOnlyList<int>>();

        var taxonomyId = 1;
        foreach (var taxonomyCode in orderedCodes)
        {
            var frameworkId = frameworkIdByTaxonomyCode[taxonomyCode];
            var framework = frameworkById[frameworkId];
            var releaseCode = releaseCodeByTaxonomyCode[taxonomyCode];
            var tableVIds = tableVIdsByTaxonomyCode[taxonomyCode].OrderBy(id => id).ToList();

            taxonomies.Add(new AccessTaxonomyRow(
                TaxonomyId: taxonomyId,
                FrameworkId: framework.FrameworkId,
                TaxonomyCode: taxonomyCode,
                // NULL here: the derivation of this row does not compute it. mTaxonomy.TaxonomyLabel
                // (and Version/PublicationDate/FromDate/ToDate) are derived later, from the
                // ModuleVersion history, by Dpm20TaxonomyMetadataCalculator.
                TaxonomyLabel: null,
                TechnicalStandard: framework.Code.ToLowerInvariant(),
                NotionalPublicationDate: null,
                ActualPublicationDate: null,
                DpmPackageCode: releaseCode,
                ConceptId: null));

            tableCounts.Add(new AccessTaxonomyTableCount(taxonomyId, tableVIds.Count));
            taxonomyKeyByTaxonomyId[taxonomyId] = $"{framework.Code.ToLowerInvariant()}/{releaseCode}";
            tableVIdsByTaxonomyId[taxonomyId] = tableVIds;

            taxonomyId++;
        }

        return new Dpm20TaxonomyDerivationResult(taxonomies, tableCounts, taxonomyKeyByTaxonomyId, tableVIdsByTaxonomyId);
    }
}
