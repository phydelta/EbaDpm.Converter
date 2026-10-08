using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Mapping.Dpm20;

/// <summary>
/// Loads the DPM 2.0 modules: <c>mModule</c>, <c>mConceptualModule</c> and
/// <c>mModuleBusinessTemplate</c>. It runs after <see cref="Dpm20AxisAndCellLoader.Load"/>, on the
/// same connection: it reads <c>mTemplateOrTable</c> already written by
/// <see cref="Dpm20StructureLoader.Load"/> to resolve the L1 node of each
/// <c>BusinessTemplateID</c>, and reuses <see cref="Dpm20StructureLoader.Result.EmittedGroupIds"/>
/// / <see cref="Dpm20StructureLoader.Result.GroupTableIdsById"/> — the computation of which groups
/// are emitted lives ONCE in <see cref="Dpm20StructureLoader"/> and is not repeated here.
///
/// This is a NEW pipeline, PARALLEL to the DPM 1.0
/// <see cref="EbaDpm.Converter.Core.Mapping.ModuleLoader"/>: it reuses only
/// <see cref="TemplateOrTableLoader.NormalizeCode"/> (the same treatment of the <c>eba_tg…</c>
/// code) and <see cref="Dpm20StructureLoader.NaturalStringComparer"/> (the same natural-order
/// criterion already used by <c>mTemplateOrTable.Order</c>) — the remaining business rules are
/// specific to DPM 2.0.
///
/// The rules were measured against the reference database (closed at 100 % except the
/// <c>Order</c> of <c>mModuleBusinessTemplate</c>, a declared choice):
///
/// <list type="bullet">
/// <item><description>
/// <b><c>mModule</c></b> ← CURRENT <c>ModuleVersion</c> (already filtered by the cutoff release in
/// <see cref="Dpm20AccessReader.ReadModuleVersions"/>), excluding the <c>DORA</c> framework AND
/// excluding <c>Module.isDocumentModule = True</c> (see <see cref="Dpm20ModuleRow"/>: without this
/// second filter 52 come out, not 50 — the 2 extra ones are <c>P3_NONREM_DIS_DOCS</c> and
/// <c>P3_REM_DIS_DOCS</c>, links to PDFs with no tables), with <c>ModuleCode</c> =
/// <b><c>UCase(ModuleVersion.Code)</c></b> (50/50). <c>ModuleLabel</c>
/// = <c>ModuleVersion.Name</c> verbatim (50/50). <c>TaxonomyID</c> is the taxonomy of the module's
/// FRAMEWORK in the CUTOFF release (4.2) — the only one a current module can hang from (all 50
/// hang from 4.2 taxonomies, none from the 13 old ones). <c>XBRLSchemaRef</c> is DERIVED (unlike
/// DPM 1.0, where it was not): pattern
/// <c>…/fws/&lt;framework&gt;/&lt;release&gt;/mod/&lt;lowercase module&gt;.xsd</c> (50/50).
/// <c>ConceptualModuleID</c> = the same synthetic id as <c>ModuleID</c> (mirror, see below).
/// <c>DefaultFrequency</c>/<c>JSONSchemaRef</c> → NULL; <c>AutogenerateRefs</c> → constant 1;
/// <c>JsonBlob</c> → empty string. <c>ConceptID</c> → NULL until the concept loader runs.
/// </description></item>
/// <item><description>
/// <b><c>mConceptualModule</c></b> is a 1:1 MIRROR of <c>mModule</c>: same id, same code, same
/// label, one row per emitted module. It does not come from any table of its own in the source.
/// </description></item>
/// <item><description>
/// <b><c>mModuleBusinessTemplate</c></b> = one (module, group) pair for each EMITTED group (the
/// 109 of <see cref="Dpm20StructureLoader"/>) to which some table of the module belongs, via
/// <c>ModuleVersionComposition</c> → <c>TableVID</c> → <c>TableID</c> → <c>TableGroupComposition</c>
/// (127/127). The <c>BusinessTemplateID</c> is the <c>Level</c> 1 / <c>TableGroup</c> node of
/// <c>mTemplateOrTable</c> for that same <c>(TaxonomyID, group code)</c> — 127/127 (the EIOPA
/// documentation says L2/L3, the data say L1). <b>The <c>Order</c> is a DECLARED CHOICE, NOT
/// derived</b>: 0-based rank by NATURAL order of the RAW group code (the same criterion as
/// <c>mTemplateOrTable.Order</c>), chosen for internal consistency — not because it matches more
/// than the other four candidates tried (all between 40/127 and 81/127).
/// </description></item>
/// </list>
/// </summary>
public static class Dpm20ModuleLoader
{
    /// <summary>Count of rows written, for the CLI report.</summary>
    public sealed record Result(
        int ModuleRows,
        int ConceptualModuleRows,
        int ModuleBusinessTemplateRows,
        IReadOnlyList<string> UnresolvedCases);

    public static Result Load(
        Dpm20AccessReader reader,
        SqliteConnection destination,
        IReadOnlyList<Dpm20ModuleRow> modules,
        IReadOnlyList<Dpm20ModuleVersionRow> currentModuleVersions,
        IReadOnlyList<Dpm20ModuleVersionCompositionRow> moduleVersionCompositions,
        IReadOnlyList<Dpm20TableVersionRow> currentTableVersions,
        IReadOnlyList<Dpm20FrameworkRow> frameworks,
        IReadOnlyList<AccessTaxonomyRow> selectedTaxonomies,
        Dpm20StructureLoader.Result structure)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(currentModuleVersions);
        ArgumentNullException.ThrowIfNull(moduleVersionCompositions);
        ArgumentNullException.ThrowIfNull(currentTableVersions);
        ArgumentNullException.ThrowIfNull(frameworks);
        ArgumentNullException.ThrowIfNull(selectedTaxonomies);
        ArgumentNullException.ThrowIfNull(structure);

        var unresolvedCases = new List<string>();
        var cutoffReleaseCode = reader.CutoffReleaseCode;

        var moduleById = modules.ToDictionary(m => m.ModuleId);
        var frameworkById = frameworks.ToDictionary(f => f.FrameworkId);
        var taxonomyById = selectedTaxonomies.ToDictionary(t => t.TaxonomyId);

        // (FrameworkID, release) -> TaxonomyID: only the CUTOFF release is needed (the 50 current
        // modules always hang from the 4.2 taxonomy of their framework).
        var taxonomyIdByFrameworkAndRelease = selectedTaxonomies
            .Where(t => t.FrameworkId.HasValue)
            .ToDictionary(t => (t.FrameworkId!.Value, t.DpmPackageCode), t => t.TaxonomyId);

        // ---- Selection of emitted modules: current, without DORA, with the 4.2 taxonomy of their
        // framework present in the current selection. ----
        var emitted = new List<(int ModuleVId, string ModuleCode, string ModuleLabel, int TaxonomyId)>();

        foreach (var mv in currentModuleVersions)
        {
            if (!moduleById.TryGetValue(mv.ModuleId, out var moduleRow))
            {
                unresolvedCases.Add(
                    $"ModuleVersion ModuleVID={mv.ModuleVId} ('{mv.Code}'): ModuleID={mv.ModuleId} "
                    + "missing from [Module].");
                continue;
            }

            if (!frameworkById.TryGetValue(moduleRow.FrameworkId, out var framework))
            {
                unresolvedCases.Add(
                    $"ModuleVersion ModuleVID={mv.ModuleVId} ('{mv.Code}'): FrameworkID="
                    + $"{moduleRow.FrameworkId} missing from Framework.");
                continue;
            }

            if (string.Equals(framework.Code, "DORA", StringComparison.OrdinalIgnoreCase))
            {
                continue; // DORA is excluded by rule, it is not an unresolved case.
            }

            if (moduleRow.IsDocumentModule)
            {
                // See Dpm20ModuleRow: without this 52 come out, not 50 — the 2 extra ones are links
                // to PDFs (P3_NONREM_DIS_DOCS, P3_REM_DIS_DOCS), with no tables and absent from the
                // reference database. It is not an unresolved case, it is the rule.
                continue;
            }

            if (!taxonomyIdByFrameworkAndRelease.TryGetValue(
                    (framework.FrameworkId, cutoffReleaseCode), out var taxonomyId))
            {
                unresolvedCases.Add(
                    $"Module '{mv.Code}' (framework '{framework.Code}'): no taxonomy "
                    + $"{framework.Code.ToLowerInvariant()} {cutoffReleaseCode} in the current selection.");
                continue;
            }

            emitted.Add((mv.ModuleVId, mv.Code.ToUpperInvariant(), mv.Name, taxonomyId));
        }

        // Deterministic order (TaxonomyID, ordinal ModuleCode) so that the synthetic ModuleID is
        // reproducible between runs, same criterion as the DPM 1.0 ModuleLoader.
        var ordered = emitted
            .OrderBy(m => m.TaxonomyId)
            .ThenBy(m => m.ModuleCode, StringComparer.Ordinal)
            .ToList();

        // Sequential synthetic ModuleID; ConceptualModuleID = ModuleID (mirror).
        var moduleIdByModuleVId = new Dictionary<int, int>();
        var nextModuleId = 1;
        foreach (var m in ordered)
        {
            moduleIdByModuleVId[m.ModuleVId] = nextModuleId++;
        }

        // ---- mModule ----
        using (var moduleWriter = new SqliteBatchWriter(
            destination,
            "mModule",
            [
                "ModuleID", "TaxonomyID", "ModuleCode", "ModuleLabel", "ConceptualModuleID",
                "DefaultFrequency", "ConceptID", "XBRLSchemaRef", "JSONSchemaRef",
                "AutogenerateRefs", "JsonBlob",
            ]))
        {
            foreach (var m in ordered)
            {
                var moduleId = moduleIdByModuleVId[m.ModuleVId];
                var taxonomy = taxonomyById[m.TaxonomyId];

                // One-line pattern, 50/50 — a real improvement of the new source (in DPM 1.0 the
                // XBRLSchemaRef was NOT derivable).
                var xbrlSchemaRef =
                    $"http://www.eba.europa.eu/eu/fr/xbrl/crr/fws/{taxonomy.TechnicalStandard}/"
                    + $"{taxonomy.DpmPackageCode}/mod/{m.ModuleCode.ToLowerInvariant()}.xsd";

                moduleWriter.AddRow(
                    moduleId,
                    m.TaxonomyId,
                    m.ModuleCode, // UCase(ModuleVersion.Code)
                    m.ModuleLabel, // ModuleVersion.Name, verbatim
                    moduleId, // ConceptualModuleID = ModuleID (mirror)
                    null, // DefaultFrequency: no source, NULL
                    null, // ConceptID: NULL until the concept loader runs
                    xbrlSchemaRef,
                    null, // JSONSchemaRef: no source, NULL
                    true, // AutogenerateRefs: constant 1
                    Array.Empty<byte>()); // JsonBlob: empty BLOB, NOT NULL
            }
        }

        // ---- mConceptualModule: 1:1 mirror of mModule, with no concept of its own ----
        using (var conceptualModuleWriter = new SqliteBatchWriter(
            destination,
            "mConceptualModule",
            ["ConceptualModuleID", "ConceptualModuleCode", "ConceptualModuleLabel"]))
        {
            foreach (var m in ordered)
            {
                var moduleId = moduleIdByModuleVId[m.ModuleVId];
                conceptualModuleWriter.AddRow(moduleId, m.ModuleCode, m.ModuleLabel);
            }
        }

        var businessTemplateRows = LoadModuleBusinessTemplates(
            destination, reader, ordered, moduleIdByModuleVId, moduleVersionCompositions,
            currentTableVersions, structure, unresolvedCases);

        return new Result(
            ModuleRows: ordered.Count,
            ConceptualModuleRows: ordered.Count,
            ModuleBusinessTemplateRows: businessTemplateRows,
            UnresolvedCases: unresolvedCases);
    }

    // ------------------------------------------------------------------
    // mModuleBusinessTemplate: (module, EMITTED group of its tables). The set of emitted groups
    // and their tables comes ALREADY COMPUTED by Dpm20StructureLoader — here it is only crossed
    // with the tables of each module and the corresponding L1 node is resolved.
    // ------------------------------------------------------------------

    private static int LoadModuleBusinessTemplates(
        SqliteConnection destination,
        Dpm20AccessReader reader,
        IReadOnlyList<(int ModuleVId, string ModuleCode, string ModuleLabel, int TaxonomyId)> orderedModules,
        IReadOnlyDictionary<int, int> moduleIdByModuleVId,
        IReadOnlyList<Dpm20ModuleVersionCompositionRow> moduleVersionCompositions,
        IReadOnlyList<Dpm20TableVersionRow> currentTableVersions,
        Dpm20StructureLoader.Result structure,
        List<string> unresolvedCases)
    {
        if (orderedModules.Count == 0)
        {
            return 0;
        }

        var tableVersionByTableVId = currentTableVersions.ToDictionary(tv => tv.TableVId);

        var tableVIdsByModuleVId = moduleVersionCompositions
            .GroupBy(c => c.ModuleVId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.TableVId).ToList());

        // Current TableGroup: same universe as Dpm20StructureLoader (122 rows, filtered by the cutoff release).
        var tableGroupCodeById = reader.ReadTableGroups()
            .ToDictionary(g => g.TableGroupId, g => g.Code);

        // L1 of mTemplateOrTable, already written by Dpm20StructureLoader on this same connection.
        var l1NodeIdByTaxonomyAndCode = ReadLevel1TemplateOrTableNodes(destination);

        // Per module, the EMITTED groups that contain any of its tables, with the RAW group code
        // (without eba_tg, not normalized) for the natural order.
        var groupsByModule = new List<(int ModuleId, int TaxonomyId, int GroupId, string RawCode)>();

        foreach (var module in orderedModules)
        {
            if (!tableVIdsByModuleVId.TryGetValue(module.ModuleVId, out var tableVIds))
            {
                continue; // no ModuleVersionComposition: the module contributes no rows.
            }

            var moduleId = moduleIdByModuleVId[module.ModuleVId];

            var moduleTableIds = new HashSet<int>();
            foreach (var tableVId in tableVIds)
            {
                if (tableVersionByTableVId.TryGetValue(tableVId, out var tv))
                {
                    moduleTableIds.Add(tv.TableId);
                }
                else
                {
                    // Reference by *VID (ModuleVersionComposition.TableVID) that today always
                    // resolves (0 failures measured, 1,089 pairs in 4.2, 1,162 in 4.3) — but
                    // without a warning, the day the source re-versions the TableVersion without
                    // propagating the TableVID to this composition it would fail silently. There
                    // is NO fallback here: it has not been measured that resolving by the stable
                    // ID (TableID) is unambiguous or safe on this path, so it is not derived by
                    // analogy. Only a trace is left, naming the case.
                    unresolvedCases.Add(
                        $"ModuleVID={module.ModuleVId}: ModuleVersionComposition.TableVID={tableVId} does not resolve "
                        + "in a current TableVersion.");
                }
            }

            if (moduleTableIds.Count == 0)
            {
                continue;
            }

            var matchedGroupIds = structure.EmittedGroupIds
                .Where(g => structure.GroupTableIdsById.TryGetValue(g, out var tables)
                    && tables.Overlaps(moduleTableIds))
                .ToList();

            foreach (var groupId in matchedGroupIds)
            {
                if (!tableGroupCodeById.TryGetValue(groupId, out var rawCode))
                {
                    // Should not happen: EmittedGroupIds/GroupTableIdsById come from the current
                    // TableGroup (same universe read here).
                    throw new InvalidOperationException(
                        $"TableGroupID={groupId} (emitted group) missing from TableGroup.");
                }

                groupsByModule.Add((moduleId, module.TaxonomyId, groupId, rawCode));
            }
        }

        var rows = 0;
        using var writer = new SqliteBatchWriter(
            destination, "mModuleBusinessTemplate", ["ModuleID", "Order", "BusinessTemplateID"]);

        foreach (var moduleGroup in groupsByModule.GroupBy(g => g.ModuleId).OrderBy(g => g.Key))
        {
            // Declared choice, NOT derived. 0-based rank by NATURAL order of the RAW group code —
            // the SAME criterion as mTemplateOrTable.Order, reused for internal consistency, not
            // because it matches more than the other candidates tried (all between 40/127 and
            // 81/127; this very criterion matches 79/127).
            var orderedGroups = moduleGroup
                .OrderBy(g => g.RawCode, Dpm20StructureLoader.NaturalStringComparer.Instance)
                .ToList();

            for (var order = 0; order < orderedGroups.Count; order++)
            {
                var entry = orderedGroups[order];
                var l1Code = "eba_tg" + TemplateOrTableLoader.NormalizeCode(entry.RawCode);

                if (!l1NodeIdByTaxonomyAndCode.TryGetValue((entry.TaxonomyId, l1Code), out var businessTemplateId))
                {
                    // Should not happen: if the group was emitted with some table of this same
                    // taxonomy (Dpm20StructureLoader processes the same selected taxonomies),
                    // Dpm20StructureLoader already created an L1 node for (TaxonomyID, code).
                    unresolvedCases.Add(
                        $"ModuleID={entry.ModuleId}: no L1 node in mTemplateOrTable for "
                        + $"TaxonomyID={entry.TaxonomyId}, code '{l1Code}' (TableGroupID={entry.GroupId}).");
                    continue;
                }

                writer.AddRow(entry.ModuleId, order, businessTemplateId);
                rows++;
            }
        }

        return rows;
    }

    private static Dictionary<(int TaxonomyId, string Code), int> ReadLevel1TemplateOrTableNodes(
        SqliteConnection destination)
    {
        var result = new Dictionary<(int, string), int>();

        using var command = destination.CreateCommand();
        command.CommandText =
            """
            SELECT "TemplateOrTableID", "TaxonomyID", "TemplateOrTableCode"
            FROM mTemplateOrTable
            WHERE "TemplateOrTableType" = 'TableGroup' AND "Level" = 1
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var templateOrTableId = reader.GetInt32(0);
            var taxonomyId = reader.GetInt32(1);
            var code = reader.GetString(2);
            result[(taxonomyId, code)] = templateOrTableId;
        }

        return result;
    }
}
