using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Mapping;

/// <summary>
/// Loads the modules: <c>mModule</c>, <c>mConceptualModule</c> and <c>mModuleBusinessTemplate</c>.
/// It runs after <see cref="TemplateOrTableLoader.Load"/>, on the same connection: it reads the
/// already written <c>mTemplateOrTable</c> to resolve the L1 node of each
/// <c>BusinessTemplateID</c>.
///
/// <list type="bullet">
/// <item><description>
/// <b><c>mModule</c></b> comes from <c>Access.Module</c>, filtered by the selected taxonomies.
/// <c>XBRLSchemaRef</c> is emitted <b>verbatim</b> from <c>Module.XbrlSchemaRef</c> (populated in
/// 415/415 rows); it is <b>not</b> deduced or rebuilt. Five rows of the 3.2 reference
/// (<c>IF_CLASS2</c>, <c>IF_CLASS3</c>, <c>IF_GROUPTEST</c>, <c>IF_TM</c>, <c>GSII</c>) differ on
/// purpose: this is a declared divergence, not a defect nor a relaxation. <c>DefaultFrequency</c>
/// and <c>JSONSchemaRef</c> are NULL; <c>AutogenerateRefs</c> is the constant 1; <c>JsonBlob</c>
/// is an empty BLOB <c>X''</c>, never NULL.
/// </description></item>
/// <item><description>
/// <b><c>mConceptualModule</c></b> is a <b>synthetic 1:1 copy of <c>mModule</c></b>: same
/// <c>ID</c>, same code, same label, one row per <i>emitted</i> module, without deduplicating even
/// if several releases of the same framework are selected. It does <b>not</b> come from
/// <c>Access.ConceptualModule</c> (55 rows, none of them is emitted), and it has no concept of
/// its own: <c>ConceptType = 'ConceptualModule'</c> does not exist in any reference.
/// </description></item>
/// <item><description>
/// <b><c>mModuleBusinessTemplate</c></b> is derived from <c>ModuleTableVersion</c> +
/// <c>TaxonomyTableVersion</c>, <b>not</b> from <c>ModuleTableOrGroup</c>:
/// <c>ModuleTableOrGroup</c> is incomplete in Access v4.1 (20 of 415 modules have tables and no
/// row there). For each <c>TableVID</c> of the module's <c>ModuleTableVersion</c>, its
/// <c>TableGroupID</c> is resolved in <c>TaxonomyTableVersion</c> filtering by the module's own
/// <c>TaxonomyID</c>; the combination is done <b>in memory</b> because Access does not support a
/// compound <c>ON</c> in a <c>LEFT JOIN</c>. That <c>TableGroupID</c> is the L1 node of
/// <c>mTemplateOrTable</c> (the same node code that <see cref="TemplateOrTableLoader.NormalizeCode"/>
/// produces for L1), and its <c>TemplateOrTableID</c> is the <c>BusinessTemplateID</c>. The
/// <c>Order</c> is ordinal alphabetical by the L1 node <b>code</b> (not the label), dense and
/// 0-based; groups are deduplicated by <c>TableGroupID</c> before numbering because the PK is
/// (<c>ModuleID</c>, <c>Order</c>).
/// </description></item>
/// <item><description>
/// <b><c>mTaxonomyPackage</c>, <c>mRewriteURI</c>, <c>mNamespacePrefix</c>, <c>mResourceFile</c>,
/// <c>mXbrlExportConfiguration</c></b>: no source in Access, created empty. They are not touched
/// here: they already come out empty from <see cref="Sqlite.SchemaCreator"/>.
/// </description></item>
/// </list>
///
/// Out of scope for this loader: data point signatures (they depend on
/// <c>mAxis</c>/<c>mAxisOrdinate</c>/<c>mOrdinateCategorisation</c>/<c>mTableCell</c>) and
/// validation rules (<c>vValidationRuleExpressions</c>/<c>vValidationRuleTables</c>: out of scope,
/// created empty).
///
/// Each <c>Module.ConceptID</c> is seeded into <c>mConceptTranslation</c> with its own
/// <c>ModuleLabel</c> (<see cref="ConceptTranslationWriter.Write"/>), as is already done for the
/// dictionary. <c>mModule.JsonBlob</c> is an empty BLOB, not NULL, the same pattern applied to
/// <c>mTable.JsonBlob</c> in <see cref="TemplateOrTableLoader"/>.
/// </summary>
public static class ModuleLoader
{
    /// <summary>Row count written per table, for the CLI report.</summary>
    public sealed record Result(int ModuleRows, int ConceptualModuleRows, int ModuleBusinessTemplateRows);

    public static Result Load(
        IDpmSourceReader source,
        SqliteConnection destination,
        IReadOnlyList<AccessTaxonomyRow> selectedTaxonomies)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(selectedTaxonomies);

        var selectedTaxonomyIds = selectedTaxonomies.Select(t => t.TaxonomyId).ToHashSet();

        // Deterministic order (TaxonomyID, ordinal ModuleCode) so that the synthetic ModuleID is
        // reproducible across runs, as in TemplateOrTableLoader.
        var modules = source.ReadModules()
            .Where(m => selectedTaxonomyIds.Contains(m.TaxonomyId))
            .OrderBy(m => m.TaxonomyId)
            .ThenBy(m => m.ModuleCode, StringComparer.Ordinal)
            .ToList();

        // ModuleID is an "Artificial ID" in the EIOPA documentation: it is synthesised
        // sequentially, like TemplateOrTableID/TableID in TemplateOrTableLoader.
        // ConceptualModuleID = ModuleID, so a second sequence is not needed.
        var moduleIdByAccessId = new Dictionary<int, int>();
        var nextModuleId = 1;
        foreach (var module in modules)
        {
            moduleIdByAccessId[module.ModuleId] = nextModuleId++;
        }

        var referencedConceptIds = new HashSet<int>();

        // mConceptTranslation seeds for the ConceptID of each module, with the same ModuleLabel
        // that mModule already emits.
        var translationSeeds = new List<(int ConceptId, string? Label, string? Description)>();

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
            foreach (var module in modules)
            {
                var moduleId = moduleIdByAccessId[module.ModuleId];

                if (module.ConceptId.HasValue)
                {
                    referencedConceptIds.Add(module.ConceptId.Value);
                    translationSeeds.Add((module.ConceptId.Value, module.ModuleLabel, null));
                }

                moduleWriter.AddRow(
                    moduleId,
                    module.TaxonomyId,
                    module.ModuleCode, // verbatim (measured: literal match 13/13 and 5/5)
                    module.ModuleLabel, // verbatim (measured: literal match 13/13 and 5/5)
                    moduleId, // ConceptualModuleID = ModuleID
                    null, // DefaultFrequency: no source, NULL
                    module.ConceptId,
                    module.XbrlSchemaRef, // verbatim; deliberately differs from the 3.2 reference in 5 rows
                    null, // JSONSchemaRef: no source, NULL
                    true, // AutogenerateRefs: constant 1
                    Array.Empty<byte>()); // JsonBlob: empty BLOB X'', NOT NULL
            }
        }

        // ---- mConceptualModule: synthetic 1:1 copy of mModule, no concept of its own ----
        using (var conceptualModuleWriter = new SqliteBatchWriter(
            destination,
            "mConceptualModule",
            ["ConceptualModuleID", "ConceptualModuleCode", "ConceptualModuleLabel"]))
        {
            foreach (var module in modules)
            {
                var moduleId = moduleIdByAccessId[module.ModuleId];

                conceptualModuleWriter.AddRow(
                    moduleId, // one row per EMITTED module, not deduplicated by code
                    module.ModuleCode,
                    module.ModuleLabel);
            }
        }

        LoadNewConcepts(source, destination, referencedConceptIds);
        ConceptTranslationWriter.Write(destination, translationSeeds);

        var businessTemplateRows = LoadModuleBusinessTemplates(source, destination, modules, moduleIdByAccessId);

        return new Result(
            ModuleRows: modules.Count,
            ConceptualModuleRows: modules.Count,
            ModuleBusinessTemplateRows: businessTemplateRows);
    }

    // ------------------------------------------------------------------
    // mConcept: creation of the ConceptID = 'Module' entries introduced by
    // Access.Module.ConceptID. They are always emitted with ReleaseID NULL, like
    // TemplateOrTable/Table.
    // ------------------------------------------------------------------

    private static void LoadNewConcepts(
        IDpmSourceReader source, SqliteConnection destination, HashSet<int> referencedConceptIds)
    {
        if (referencedConceptIds.Count == 0)
        {
            return;
        }

        var alreadyLoaded = new HashSet<int>();
        using (var command = destination.CreateCommand())
        {
            command.CommandText = "SELECT \"ConceptID\" FROM mConcept";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                alreadyLoaded.Add(reader.GetInt32(0));
            }
        }

        var newIds = referencedConceptIds.Except(alreadyLoaded).ToList();
        if (newIds.Count == 0)
        {
            return;
        }

        using var writer = new SqliteBatchWriter(
            destination,
            "mConcept",
            ["ConceptID", "ConceptType", "OwnerID", "ReleaseID", "CreationDate", "ModificationDate", "FromDate", "ToDate"]);

        foreach (var concept in source.ReadConceptsByIds(newIds))
        {
            writer.AddRow(
                concept.ConceptId,
                concept.ConceptType, // 'Module', verbatim from Access (measured: 100% of Module ConceptIDs)
                concept.OwnerId,
                null, // Module -> ReleaseID always NULL
                concept.CreationDate,
                concept.ModificationDate,
                concept.FromDate,
                concept.ToDate);
        }
    }

    // ------------------------------------------------------------------
    // mModuleBusinessTemplate: derived from ModuleTableVersion + TaxonomyTableVersion,
    // resolved against the L1 of mTemplateOrTable already written by TemplateOrTableLoader.
    // ------------------------------------------------------------------

    private static int LoadModuleBusinessTemplates(
        IDpmSourceReader source,
        SqliteConnection destination,
        IReadOnlyList<AccessModuleRow> modules,
        IReadOnlyDictionary<int, int> moduleIdByAccessId)
    {
        if (modules.Count == 0)
        {
            return 0;
        }

        var selectedTaxonomyIds = modules.Select(m => m.TaxonomyId).ToHashSet();

        // (TaxonomyID, TableVID) -> TableGroupID: in-memory combination of TaxonomyTableVersion,
        // because Access does not support a compound ON in a LEFT JOIN.
        var tableGroupIdByTaxonomyAndTableVId = source.ReadTaxonomyTableVersions()
            .Where(v => selectedTaxonomyIds.Contains(v.TaxonomyId) && v.TableGroupId.HasValue)
            .ToDictionary(v => (v.TaxonomyId, v.TableVId), v => v.TableGroupId!.Value);

        // ModuleID (Access) -> TableVIDs of ModuleTableVersion, only for the emitted modules.
        var tableVIdsByAccessModuleId = source.ReadModuleTableVersions()
            .Where(mtv => moduleIdByAccessId.ContainsKey(mtv.ModuleId))
            .GroupBy(mtv => mtv.ModuleId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.TableVId).ToList());

        var tableGroupCodeById = source.ReadTableGroups()
            .ToDictionary(g => g.TableGroupId, g => g.TableGroupCode);

        // L1 of mTemplateOrTable, already written by TemplateOrTableLoader on this same connection.
        var l1NodeIdByTaxonomyAndCode = ReadLevel1TemplateOrTableNodes(destination);

        // Per module, the DEDUPLICATED set of TableGroupIDs (the PK of mModuleBusinessTemplate
        // is (ModuleID, Order): a repeated Order breaks the insert).
        var groupsByModule = new List<(int ModuleId, int TaxonomyId, int TableGroupId, string Code)>();

        foreach (var module in modules)
        {
            if (!tableVIdsByAccessModuleId.TryGetValue(module.ModuleId, out var tableVIds))
            {
                continue; // no ModuleTableVersion: the module contributes no rows (measured: 23/415)
            }

            var destModuleId = moduleIdByAccessId[module.ModuleId];
            var tableGroupIds = new HashSet<int>();

            foreach (var tableVId in tableVIds)
            {
                if (tableGroupIdByTaxonomyAndTableVId.TryGetValue((module.TaxonomyId, tableVId), out var groupId))
                {
                    tableGroupIds.Add(groupId);
                }
            }

            foreach (var groupId in tableGroupIds)
            {
                if (!tableGroupCodeById.TryGetValue(groupId, out var groupCode))
                {
                    // Should not happen: TaxonomyTableVersion.TableGroupID always points to a
                    // real TableGroup row (Access's own FK).
                    throw new InvalidOperationException(
                        $"TaxonomyTableVersion references TableGroupID={groupId}, which is missing from TableGroup.");
                }

                var code = "eba_tg" + TemplateOrTableLoader.NormalizeCode(groupCode);
                groupsByModule.Add((destModuleId, module.TaxonomyId, groupId, code));
            }
        }

        var rows = 0;
        using var writer = new SqliteBatchWriter(
            destination,
            "mModuleBusinessTemplate",
            ["ModuleID", "Order", "BusinessTemplateID"]);

        foreach (var moduleGroup in groupsByModule.GroupBy(g => g.ModuleId).OrderBy(g => g.Key))
        {
            // Order is ordinal alphabetical by the L1 node code (not the label), dense, 0-based.
            // Groups are already deduplicated by TableGroupID (HashSet above).
            var ordered = moduleGroup.OrderBy(g => g.Code, StringComparer.Ordinal).ToList();

            for (var order = 0; order < ordered.Count; order++)
            {
                var entry = ordered[order];

                if (!l1NodeIdByTaxonomyAndCode.TryGetValue((entry.TaxonomyId, entry.Code), out var businessTemplateId))
                {
                    // Should not happen: TemplateOrTableLoader writes an L1 node for every
                    // TableGroup of the selected taxonomies, with no further filter.
                    throw new InvalidOperationException(
                        $"The mTemplateOrTable L1 node was not found for TaxonomyID={entry.TaxonomyId}, "
                        + $"code '{entry.Code}' (TableGroupID={entry.TableGroupId}).");
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
