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
/// unchanged. 32 rows against the reference source: all 31 of the reference database, plus
/// <c>pay 4.1</c>.
/// </param>
/// <param name="TableCounts">Count of table versions per taxonomy, in the same shape <c>--list-taxonomies</c> uses today for DPM 1.0.</param>
/// <param name="TaxonomyKeyByTaxonomyId">The <c>TaxonomyKey</c> of each derived taxonomy: <c>lowercase-framework/release</c>, e.g. <c>corep/4.2</c>.</param>
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
/// Rules (all verified against the source and against the reference database):
///
/// 1. **Universe**: current table versions (already filtered by the cutoff release in
///    <see cref="Dpm20AccessReader.ReadTableVersions"/>) that are in some current module, via
///    <c>ModuleVersionComposition</c> → current <c>ModuleVersion</c>.
/// 1b. <b>Abstract closure</b>: if the composition row declares an ABSTRACT table, its CURRENT
///    concrete children are added as well (<c>TableVersion.AbstractTableID</c> pointing to that
///    abstract table), even if they have no row of their own in <c>ModuleVersionComposition</c>
///    for that module. It is a rule of the Access database, backed by the invariant "a module
///    that declares an abstract table declares all its current children" (394/395 in 4.2, 408/408
///    in 4.3; the only failure in the corpus is <c>IF_CLASS2 → C_34.02.b</c>, which Access itself
///    restores in 4.3). It adds +1 (module, table) pair in 4.2 and +0 in 4.3 — it is NOT
///    propagated through <c>TableGroup</c> (that route over-produced 26 extra pairs).
/// 2. **Framework of a table version**: <c>ModuleVersionComposition</c> → <c>ModuleVersion</c>
///    → <c>[Module].FrameworkID</c> → <c>Framework.Code</c>. A table can fall in SEVERAL
///    frameworks (unlike DPM 1.0, where the relation was functional): it is modelled with a set,
///    not with a single id.
/// 3. **Emitted universe**: abstract tables and those of framework <c>DORA</c> are removed → 788 codes.
/// 4. **Each emitted table version produces TWO pairs** (framework, release): one with the cutoff
///    release and one with its <c>StartReleaseID</c>. If they coincide, a single pair. The
///    intermediate release NEVER generates a pair.
/// 5. <c>TaxonomyCode</c> = lowercase framework code + space + release code.
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
        var cutoffReleaseCode = releaseCodeById.TryGetValue(cutoffReleaseId, out var code)
            ? code
            : throw new InvalidOperationException(
                $"The cutoff release (ReleaseID={cutoffReleaseId}) is not in the provided release list.");

        var frameworkById = frameworks.ToDictionary(f => f.FrameworkId);
        var doraFrameworkId = frameworks
            .Where(f => string.Equals(f.Code, "DORA", StringComparison.OrdinalIgnoreCase))
            .Select(f => (int?)f.FrameworkId)
            .FirstOrDefault();

        var frameworkIdByModuleId = modules.ToDictionary(m => m.ModuleId, m => m.FrameworkId);
        var moduleIdByModuleVId = currentModuleVersions.ToDictionary(mv => mv.ModuleVId, mv => mv.ModuleId);
        var tableVersionByTableVId = currentTableVersions.ToDictionary(tv => tv.TableVId);

        // Index for the abstract closure (rule 1b): abstract TableID -> TableVID of its CURRENT
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

        // Step 1: universe — table versions current in some current module, and the set of
        // frameworks each one belongs to (rules 1 and 2), closed with the abstract closure
        // (rule 1b).
        var frameworkIdsByTableVId = new Dictionary<int, HashSet<int>>();

        void AddFramework(int tableVId, int frameworkId)
        {
            if (!frameworkIdsByTableVId.TryGetValue(tableVId, out var set))
            {
                set = [];
                frameworkIdsByTableVId[tableVId] = set;
            }

            set.Add(frameworkId);
        }

        foreach (var composition in moduleVersionCompositions)
        {
            if (!moduleIdByModuleVId.TryGetValue(composition.ModuleVId, out var moduleId))
            {
                continue; // ModuleVersion not current at the cutoff release.
            }

            if (!tableVersionByTableVId.TryGetValue(composition.TableVId, out var tableVersion))
            {
                continue; // TableVersion not current at the cutoff release.
            }

            if (!frameworkIdByModuleId.TryGetValue(moduleId, out var frameworkId))
            {
                throw new InvalidOperationException(
                    $"[Module] has no ModuleID={moduleId} (referenced from a current ModuleVersion); "
                    + "the source has changed from what was assumed.");
            }

            AddFramework(composition.TableVId, frameworkId);

            // Rule 1b: the row declares an ABSTRACT table -> close it with its current concrete
            // children, under the SAME framework, even if the composition does not carry their
            // explicit row (case IF_CLASS2 -> C_34.02, abstract; children C_34.02.a declared and
            // C_34.02.b not declared).
            if (tableVersion.IsAbstract
                && concreteChildrenTableVIdsByAbstractTableId.TryGetValue(tableVersion.TableId, out var children))
            {
                foreach (var childTableVId in children)
                {
                    AddFramework(childTableVId, frameworkId);
                }
            }
        }

        // Step 2: emitted universe — no abstract tables, no tables that fall ONLY in DORA (rule 3).
        var emittedTableVIds = frameworkIdsByTableVId
            .Where(kvp =>
            {
                var tableVersion = tableVersionByTableVId[kvp.Key];
                if (tableVersion.IsAbstract)
                {
                    return false;
                }

                var frameworkIds = kvp.Value;
                var isDoraOnly = doraFrameworkId is { } doraId
                    && frameworkIds.Count == 1
                    && frameworkIds.Contains(doraId);
                return !isDoraOnly;
            })
            .Select(kvp => kvp.Key)
            .ToList();

        // Step 3: the (table version, taxonomy) pairs — rules 4 and 5.
        var tableVIdsByTaxonomyCode = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var frameworkIdByTaxonomyCode = new Dictionary<string, int>(StringComparer.Ordinal);
        var releaseCodeByTaxonomyCode = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var tableVId in emittedTableVIds)
        {
            var tableVersion = tableVersionByTableVId[tableVId];
            var frameworkIds = frameworkIdsByTableVId[tableVId];

            foreach (var frameworkId in frameworkIds)
            {
                if (doraFrameworkId is { } doraId && frameworkId == doraId)
                {
                    continue; // DORA is not emitted as a pair, even if the table also falls in another framework.
                }

                var framework = frameworkById[frameworkId];
                var frameworkCodeLower = framework.Code.ToLowerInvariant();

                if (!releaseCodeById.TryGetValue(tableVersion.StartReleaseId, out var startReleaseCode))
                {
                    throw new InvalidOperationException(
                        $"TableVersion.TableVID={tableVId} has StartReleaseID={tableVersion.StartReleaseId} "
                        + "with no matching row in [Release].");
                }

                AddPair(frameworkCodeLower, cutoffReleaseCode, framework.FrameworkId);
                AddPair(frameworkCodeLower, startReleaseCode, framework.FrameworkId);

                void AddPair(string fwCodeLower, string releaseCode, int fwId)
                {
                    var taxonomyCode = $"{fwCodeLower} {releaseCode}";

                    if (!tableVIdsByTaxonomyCode.TryGetValue(taxonomyCode, out var set))
                    {
                        set = [];
                        tableVIdsByTaxonomyCode[taxonomyCode] = set;
                        frameworkIdByTaxonomyCode[taxonomyCode] = fwId;
                        releaseCodeByTaxonomyCode[taxonomyCode] = releaseCode;
                    }

                    set.Add(tableVId);
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
