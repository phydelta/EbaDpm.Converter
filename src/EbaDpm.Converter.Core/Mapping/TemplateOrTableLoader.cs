using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Mapping;

/// <summary>
/// Loads the template and table tree: <c>mTemplateOrTable</c>, <c>mTable</c> and
/// <c>mTaxonomyTable</c>. It runs after <see cref="DictionaryLoader.Load"/>, on the same
/// connection.
///
/// Three levels in <c>mTemplateOrTable</c>, with two different sources and one common type
/// <c>"TableGroup"</c> for the first two:
///
/// <list type="bullet">
/// <item><description>
/// L1 (<c>TemplateOrTableType='TableGroup'</c>, <c>Level=1</c>) comes from <c>Access.TableGroup</c>,
/// one per taxonomy. It does <b>not</b> come from <c>Access.TemplateGroup</c> (that tree is per
/// framework and has no demonstrated target).
/// </description></item>
/// <item><description>
/// L2 (<c>TemplateOrTableType='TableGroup'</c>, <c>Level=2</c>) comes from <c>Access.Template</c>,
/// one per distinct <c>(TaxonomyID, TemplateID, TableGroupID)</c> triple of
/// <c>Access.TaxonomyTableVersion</c>. Using this table instead of <c>TableGroupTemplates</c>
/// applies the taxonomy filter naturally; they are equivalent (262/271 pairs in both cases).
/// </description></item>
/// <item><description>
/// L3 (<c>TemplateOrTableType='BusinessTable'</c>, <c>Level=1</c>) comes from
/// <c>Access.TableVersion</c>. <c>mTable</c> is <b>deduplicated by <c>TableVID</c></b>:
/// <c>mTable.TableID</c> is a single-column PK, so there cannot be two rows for the same table
/// version even if several selected taxonomies reuse it (measured: it does happen, 44-54 cases
/// among the 13 common taxonomies, e.g. <c>REM 3.5</c>/<c>REM 4.0</c> share a <c>TableVID</c>).
/// That reuse is expressed in <c>mTaxonomyTable</c> (one row per <c>(TaxonomyID, TableVID)</c>).
/// </description></item>
/// </list>
///
/// <b>Synthetic IDs.</b> <c>TemplateOrTableID</c> and <c>mTable.TableID</c> are "Artificial IDs"
/// per the EIOPA documentation: they are synthesised sequentially, like <c>HierarchyNodeID</c> in
/// <see cref="DictionaryLoader"/>, instead of reusing Access IDs. Reusing them would not be safe
/// here: <c>TableGroupID</c>, <c>TemplateID</c> and <c>TableVID</c> are three independent
/// numberings that could coincide numerically without being the same node.
///
/// <b>Documented assumption, without positive evidence in any reference</b> (none of the three
/// contains overlapping content between taxonomies of the same framework that would allow
/// verifying it):
/// <list type="bullet">
/// <item><description>
/// <c>mTaxonomyTable.IsTableSource</c> has no source in Access (EIOPA does not even document it).
/// It is emitted as constant <c>true</c>, the only value observed in the 4.0/4.2 references
/// (151/151 and 846/846).
/// </description></item>
/// </list>
///
/// <b>Exact rules (measured, no longer assumptions):</b>
/// <list type="bullet">
/// <item><description>
/// <c>mTable.TableLabel</c> and <c>mTemplateOrTable(BusinessTable).TemplateOrTableLabel</c> are
/// <c>TableVersion.XbrlTableCode + ":" + TableVersion.TableVersionLabel</c>: 151/151 (100 %)
/// against the 4.0 reference; 96.84 % against 4.2 (the failures are DPM 2.0 artifacts). 4.0 and
/// 4.2 agree against 3.2 (which uses another convention), so the 4.0/4.2 form is emitted.
/// </description></item>
/// <item><description>
/// <c>mTaxonomyTable.IsSimplyReuse</c> is emitted as constant <c>true</c>: it is 0 in 3.2 but 1 in
/// 4.0 and 4.2 (constant in all three, but different), and 4.0 + 4.2 win the tie against 3.2. It
/// is not <c>TaxonomyTableVersion.IsSimpleReuse</c> verbatim (that Access column does vary row by
/// row, but it is not what is emitted here).
/// </description></item>
/// <item><description>
/// When a <c>TableVID</c> is shared by several selected taxonomies there is <b>no "primary"
/// row</b>: each (<c>TaxonomyID</c>, <c>TableVID</c>) association of <c>TaxonomyTableVersion</c>
/// gets its OWN <c>BusinessTable</c> node, hanging from the L2 parent of ITS OWN taxonomy. Only
/// <c>mTable</c> stays deduplicated by <c>TableVID</c> (its PK is a single column). See the
/// XML doc of <see cref="LoadLevel3BusinessTables"/> for the measurement backing this (0/0/0
/// cases of a shared <c>TableID</c> in the three references).
/// </description></item>
/// </list>
///
/// Out of scope here: <c>mAxis</c>, <c>mTableAxis</c>, <c>mAxisOrdinate</c>,
/// <c>mOrdinateCategorisation</c>, <c>mOpenAxisValueRestriction</c>, <c>mTableCell</c>,
/// <c>mCellPosition</c>, modules, signatures. Consequently <c>mTable.YDimVal</c>/<c>ZDimVal</c>
/// (derived from the axes) are left <see langword="null"/>.
///
/// <b>Four corrections measured on a full <c>--all</c> run</b> (not on 1 of 8 taxonomies):
/// <list type="bullet">
/// <item><description>
/// The <c>ConceptType</c> that <see cref="LoadNewConcepts"/> creates for
/// <c>TableGroup</c>/<c>Template</c>/<c>TableVersion</c> is translated to the TARGET vocabulary
/// with <see cref="ConceptTypeTranslation.ToDestinationVocabulary"/>
/// (<c>TableGroup</c>/<c>Template</c> → <c>TemplateOrTable</c>; <c>TableVersion</c> →
/// <c>Table</c>); the Access one is never written verbatim.
/// </description></item>
/// <item><description>
/// <b>The <c>ConceptID</c> of <c>Access.Table</c> (950 rows) is NOT emitted.</b> It used to feed
/// <c>mTemplateOrTable(BusinessTable).ConceptID</c>, but that entity produces no target row
/// (<c>mTable</c> comes from <c>TableVersion</c>, not from <c>Table</c>): its concept described
/// nothing, the same coherence defect already corrected for the synthetic <c>MET</c> dimension.
/// <see cref="LoadLevel3BusinessTables"/> no longer reads <c>Access.Table</c> at all; the
/// <c>ConceptID</c> of the <c>BusinessTable</c> row is now <c>TableVersion.ConceptID</c>, the SAME
/// concept that already identifies the corresponding <c>mTable</c> row (sharing a <c>ConceptID</c>
/// between two rows that describe the same business object is accepted and declared; it is not a
/// synthesis).
/// </description></item>
/// <item><description>
/// <c>mConceptTranslation</c> is also seeded for <c>TableGroup</c>/<c>Template</c> (their own
/// <c>*Label</c>) and for <c>TableVersion</c> (the same text already used by
/// <c>mTable.TableLabel</c>/<c>TemplateOrTableLabel</c>), through
/// <see cref="ConceptTranslationWriter.Write"/>.
/// </description></item>
/// <item><description>
/// <c>mTable.JsonBlob</c> is an empty BLOB (<c>Array.Empty&lt;byte&gt;()</c>), never
/// <see langword="null"/>, as <c>mModule.JsonBlob</c> already is (<see cref="ModuleLoader"/>).
/// </description></item>
/// </list>
/// </summary>
public static class TemplateOrTableLoader
{
    /// <summary>Row count written per table, for the CLI report.</summary>
    /// <param name="TableIdByTableVId">
    /// Map <c>Access.TableVID</c> → <c>mTable.TableID</c> (synthetic) of all the emitted table
    /// versions, deduplicated 1:1 like <c>mTable</c>. It is consumed by <c>AxisAndCellLoader</c> to
    /// resolve the <c>TableID</c> of each axis/cell.
    /// </param>
    public sealed record Result(
        int TemplateOrTableRows,
        int TableRows,
        int TaxonomyTableRows,
        IReadOnlyDictionary<int, int> TableIdByTableVId);

    public static Result Load(
        IDpmSourceReader source,
        SqliteConnection destination,
        IReadOnlyList<AccessTaxonomyRow> selectedTaxonomies)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(selectedTaxonomies);

        var selectedTaxonomyIds = selectedTaxonomies.Select(t => t.TaxonomyId).ToHashSet();

        var nextTemplateOrTableId = 1;
        var nextTableId = 1;

        // ConceptIDs of the TableGroup/Template/TableVersion emitted in the three tables: they
        // must be created in mConcept (like DictionaryLoader.LoadNewConcepts), or their FKs are
        // left orphaned. TemplateOrTable and Table are always emitted with ReleaseID NULL; unlike
        // Dimension/Member, no release suffix needs to be resolved here.
        // Access.Table NEVER contributes a ConceptID here: that entity produces no target row,
        // so its concept would not describe anything (see the class XML doc).
        var referencedConceptIds = new HashSet<int>();

        // mConceptTranslation seeds for the ConceptIDs created in this phase
        // (TableGroup/Template/TableVersion), with the same *Label that each level already emits.
        var translationSeeds = new List<(int ConceptId, string? Label, string? Description)>();

        using var templateOrTableWriter = new SqliteBatchWriter(
            destination,
            "mTemplateOrTable",
            [
                "TemplateOrTableID", "TaxonomyID", "TemplateOrTableCode", "TemplateOrTableLabel",
                "TemplateOrTableType", "Order", "Level", "ParentTemplateOrTableID", "ConceptID",
                "IsTableGroupSource", "TC", "TT", "TL", "TD", "YC", "XC",
            ]);

        var l1NodeIdByTableGroupId = LoadLevel1TableGroups(
            source, selectedTaxonomyIds, templateOrTableWriter, referencedConceptIds, translationSeeds, ref nextTemplateOrTableId);

        var selectedTaxonomyTableVersions = source.ReadTaxonomyTableVersions()
            .Where(v => selectedTaxonomyIds.Contains(v.TaxonomyId))
            .ToList();

        var l2NodeIdByTriple = LoadLevel2Templates(
            source, selectedTaxonomyTableVersions, l1NodeIdByTableGroupId, templateOrTableWriter, referencedConceptIds, translationSeeds, ref nextTemplateOrTableId);

        var (tableRows, businessTableRows, taxonomyTableRows, tableIdByTableVId) = LoadLevel3BusinessTables(
            source,
            selectedTaxonomyTableVersions,
            l2NodeIdByTriple,
            destination,
            templateOrTableWriter,
            referencedConceptIds,
            translationSeeds,
            ref nextTemplateOrTableId,
            ref nextTableId);

        // SqliteBatchWriter does not support two live instances with an open transaction on the
        // same connection: flush the pending batch of templateOrTableWriter before opening the
        // mConcept writer.
        templateOrTableWriter.Flush();
        LoadNewConcepts(source, destination, referencedConceptIds);
        ConceptTranslationWriter.Write(destination, translationSeeds);

        return new Result(
            TemplateOrTableRows: l1NodeIdByTableGroupId.Count + l2NodeIdByTriple.Count + businessTableRows,
            TableRows: tableRows,
            TaxonomyTableRows: taxonomyTableRows,
            TableIdByTableVId: tableIdByTableVId);
    }

    // ------------------------------------------------------------------
    // mConcept: new entries introduced by TableGroup/Template/TableVersion/Table.
    // Always emitted with ReleaseID NULL (unlike Dimension/Member/Taxonomy).
    // ------------------------------------------------------------------

    private static void LoadNewConcepts(
        IDpmSourceReader source, SqliteConnection destination, HashSet<int> referencedConceptIds)
    {
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
                ConceptTypeTranslation.ToDestinationVocabulary(concept.ConceptType),
                concept.OwnerId,
                null, // TemplateOrTable and Table (and by extension TableGroup/Template/TableVersion) -> ReleaseID always NULL
                concept.CreationDate,
                concept.ModificationDate,
                concept.FromDate,
                concept.ToDate);
        }
    }

    // ------------------------------------------------------------------
    // L1: Access.TableGroup, one per taxonomy
    // ------------------------------------------------------------------

    private static Dictionary<int, int> LoadLevel1TableGroups(
        IDpmSourceReader source,
        HashSet<int> selectedTaxonomyIds,
        SqliteBatchWriter templateOrTableWriter,
        HashSet<int> referencedConceptIds,
        List<(int ConceptId, string? Label, string? Description)> translationSeeds,
        ref int nextTemplateOrTableId)
    {
        var tableGroups = source.ReadTableGroups()
            .Where(g => g.TaxonomyId.HasValue && selectedTaxonomyIds.Contains(g.TaxonomyId.Value))
            .OrderBy(g => g.TaxonomyId)
            .ThenBy(g => g.TableGroupCode, StringComparer.Ordinal)
            .ToList();

        var nodeIdByTableGroupId = new Dictionary<int, int>();

        foreach (var group in tableGroups)
        {
            var nodeId = nextTemplateOrTableId++;
            nodeIdByTableGroupId[group.TableGroupId] = nodeId;

            if (group.ConceptId.HasValue)
            {
                referencedConceptIds.Add(group.ConceptId.Value);
                translationSeeds.Add((group.ConceptId.Value, group.TableGroupLabel, null));
            }

            templateOrTableWriter.AddRow(
                nodeId,
                group.TaxonomyId,
                "eba_tg" + NormalizeCode(group.TableGroupCode),
                group.TableGroupLabel,
                "TableGroup",
                0, // constant 0 at L1 (measured on the 27/111 rows of 4.0/4.2), not alphabetical
                1,
                0, // root: Parent = 0, never NULL
                group.ConceptId,
                true, // IsTableGroupSource: constant 1 in the 1,529 measured rows
                null, null, null, null, null, null); // TC/TT/TL/TD/YC/XC: "Not used" per the EIOPA documentation
        }

        return nodeIdByTableGroupId;
    }

    // ------------------------------------------------------------------
    // L2: Access.Template via TaxonomyTableVersion (L2 -> L1 link)
    // ------------------------------------------------------------------

    private static Dictionary<(int TaxonomyId, int TemplateId, int TableGroupId), int> LoadLevel2Templates(
        IDpmSourceReader source,
        IReadOnlyList<AccessTaxonomyTableVersionRow> selectedTaxonomyTableVersions,
        IReadOnlyDictionary<int, int> l1NodeIdByTableGroupId,
        SqliteBatchWriter templateOrTableWriter,
        HashSet<int> referencedConceptIds,
        List<(int ConceptId, string? Label, string? Description)> translationSeeds,
        ref int nextTemplateOrTableId)
    {
        var templatesById = source.ReadTemplates().ToDictionary(t => t.TemplateId);

        var distinctTriples = selectedTaxonomyTableVersions
            .Where(v => v.TemplateId.HasValue && v.TableGroupId.HasValue)
            .Select(v => (v.TaxonomyId, TemplateId: v.TemplateId!.Value, TableGroupId: v.TableGroupId!.Value))
            .Distinct()
            .Where(t => l1NodeIdByTableGroupId.ContainsKey(t.TableGroupId));

        // Grouped by their L1 parent: Order is alphabetical 0..N-1 within each group.
        var byParent = distinctTriples
            .Select(t => (
                Triple: t,
                ParentId: l1NodeIdByTableGroupId[t.TableGroupId],
                Template: templatesById.TryGetValue(t.TemplateId, out var tpl) ? tpl : null))
            .Where(x => x.Template is not null)
            .Select(x => (x.Triple, x.ParentId, Template: x.Template!, Code: "eba_tg" + NormalizeCode(x.Template!.TemplateCode)))
            .GroupBy(x => x.ParentId)
            .OrderBy(g => g.Key);

        var nodeIdByTriple = new Dictionary<(int, int, int), int>();

        foreach (var parentGroup in byParent)
        {
            var siblingsOrdered = parentGroup.OrderBy(x => x.Code, StringComparer.Ordinal).ToList();

            for (var order = 0; order < siblingsOrdered.Count; order++)
            {
                var (triple, parentId, template, code) = siblingsOrdered[order];
                var nodeId = nextTemplateOrTableId++;
                nodeIdByTriple[triple] = nodeId;

                if (template.ConceptId.HasValue)
                {
                    referencedConceptIds.Add(template.ConceptId.Value);
                    translationSeeds.Add((template.ConceptId.Value, template.TemplateLabel, null));
                }

                templateOrTableWriter.AddRow(
                    nodeId,
                    triple.TaxonomyId,
                    code,
                    template.TemplateLabel,
                    "TableGroup",
                    order, // alphabetical 0..N-1 within the parent (measured: 271/271)
                    2,
                    parentId,
                    template.ConceptId,
                    true,
                    null, null, null, null, null, null);
            }
        }

        return nodeIdByTriple;
    }

    // ------------------------------------------------------------------
    // L3: Access.TableVersion. mTable is deduplicated by TableVID (its PK is a single column);
    // the BusinessTable node of mTemplateOrTable, and mTaxonomyTable, are NOT deduplicated.
    // ------------------------------------------------------------------

    /// <summary>
    /// <b>One <c>BusinessTable</c> node per (<c>TaxonomyID</c>, <c>TableVID</c>) association</b>,
    /// not one per <c>TableVID</c> shared among the selected taxonomies. A previous design
    /// (total dedup, with the node hanging only from the L2 parent of the chronologically
    /// "primary" taxonomy) was wrong: when a physical table was reused across taxonomies,
    /// <c>mTaxonomyTable</c> did link the table to all of them, but <c>mTemplateOrTable</c> only
    /// had the node under the first one, so a tree filtered by <c>TaxonomyID</c> was incomplete
    /// for the rest.
    ///
    /// Measured before correcting:
    /// <list type="bullet">
    /// <item><description>
    /// Real scope on <c>--all</c> (128 taxonomies, unfiltered): of 2,561 distinct <c>TableVID</c>
    /// values, 1,143 (44.6 %) are associated with more than one selected taxonomy, up to 21
    /// taxonomies for a single <c>TableVID</c>, not always 2. The total number of
    /// (<c>TaxonomyID</c>, <c>TableVID</c>) associations that the previous design left without a
    /// node of their own is 3,575.
    /// </description></item>
    /// <item><description>
    /// What the references do: <b>none shares</b>. In all three (3.2 with 4 taxonomies, 4.0 with 5
    /// and 4.2 with 31), <c>SELECT TableID FROM mTaxonomyTable GROUP BY TableID HAVING
    /// COUNT(DISTINCT TaxonomyID) &gt; 1</c> returns <b>0 rows</b>: no reference ever leaves a
    /// <c>TableID</c> linked to more than one taxonomy. Corroborated with the <c>if 3.2</c> /
    /// <c>corep 3.2</c> case in Access (same physical <c>TableVID</c>, e.g. 2007 for
    /// <c>C_18.00</c>, same <c>ConceptID</c> 84111 in both <c>TaxonomyTableVersion</c> rows): in
    /// the 3.2 reference, <c>C_18.00</c> of <c>IF_3.2</c> is <c>TableID</c> 9 /
    /// <c>ConceptID</c> 14325 and that of <c>COREP_3.2</c> is <c>TableID</c> 140 /
    /// <c>ConceptID</c> 15824: two completely independent <c>mTable</c> rows for what in Access is
    /// the same physical row.
    /// </description></item>
    /// </list>
    /// The measurement supports duplicating the node, not sharing it. What this implementation
    /// does NOT do, and why it does not reproduce the reference 100 % literally, is invent a new
    /// <c>ConceptID</c> for each duplicate, as the reference generator apparently does: that would
    /// fabricate data with no source in Access (forbidden). Instead, all the <c>BusinessTable</c>
    /// nodes of one <c>TableVID</c>, and the <c>mTable</c> row they reference (which does remain
    /// deduplicated by its single-column PK), share the real <c>ConceptID</c> of
    /// <c>TableVersion</c>: sharing a <c>ConceptID</c> between rows that describe the same business
    /// object is accepted and declared, and is not a synthesis. The business-key census of
    /// <c>mTable</c> (<c>B-TPL-03</c>) already passed at 100 % with <c>mTable</c> deduplicated (via
    /// <c>mTaxonomyTable</c>), so duplicating <c>mTable</c> as well was not needed to close the
    /// measured gap.
    /// </summary>
    private static (int TableRows, int BusinessTableRows, int TaxonomyTableRows, IReadOnlyDictionary<int, int> TableIdByTableVId) LoadLevel3BusinessTables(
        IDpmSourceReader source,
        IReadOnlyList<AccessTaxonomyTableVersionRow> selectedTaxonomyTableVersions,
        IReadOnlyDictionary<(int TaxonomyId, int TemplateId, int TableGroupId), int> l2NodeIdByTriple,
        SqliteConnection destination,
        SqliteBatchWriter templateOrTableWriter,
        HashSet<int> referencedConceptIds,
        List<(int ConceptId, string? Label, string? Description)> translationSeeds,
        ref int nextTemplateOrTableId,
        ref int nextTableId)
    {
        var tableVersionIds = selectedTaxonomyTableVersions.Select(v => v.TableVId).Distinct().ToList();
        var tableVersionsById = source.ReadTableVersionsByIds(tableVersionIds).ToDictionary(t => t.TableVId);

        // Access.Table (950 rows) is NOT read here. That entity produces no target row (mTable
        // comes from TableVersion, not from Table), and previously its ConceptID fed
        // mTemplateOrTable(BusinessTable).ConceptID wrongly with nothing describing it: 950
        // dangling concepts. That ConceptID is now TableVersion.ConceptID, the same one that
        // identifies the corresponding mTable row AND all its BusinessTable nodes, one per
        // taxonomy that uses it.

        // ---- mTable: ONE synthetic TableID per TableVID (its PK is a single column; this does
        // not change, only the BusinessTable/mTaxonomyTable nodes below) ----
        var tableCandidates = new List<(int TableVId, AccessTableVersionRow Tv, string Code, string Label)>();

        foreach (var tableVId in tableVersionIds.OrderBy(id => id))
        {
            if (!tableVersionsById.TryGetValue(tableVId, out var tv))
            {
                // Should not happen: TaxonomyTableVersion.TableVID always points to a real
                // TableVersion row (Access's own FK). If it does, it is a data anomaly.
                throw new InvalidOperationException(
                    $"TaxonomyTableVersion references TableVID={tableVId}, which is missing from TableVersion.");
            }

            var code = tv.XbrlTableCode
                ?? throw new InvalidOperationException($"TableVersion without XbrlTableCode (TableVID={tableVId}).");

            // Exact measured rule: 151/151 = 100 % against the 4.0 reference; 4.0/4.2 win against
            // 3.2, which uses another convention.
            var label = code + ":" + tv.TableVersionLabel;

            tableCandidates.Add((tableVId, tv, code, label));
        }

        // SqliteBatchWriter does not support two live instances with an open transaction on the
        // same connection (see its type doc): flush the pending batch of templateOrTableWriter
        // (L1+L2 already written) before opening the mTable writer.
        templateOrTableWriter.Flush();

        var tableIdByTableVId = new Dictionary<int, int>();

        using (var tableWriter = new SqliteBatchWriter(
            destination,
            "mTable",
            [
                "TableID", "TableCode", "TableLabel", "FromDate", "ToDate", "XbrlFilingIndicatorCode",
                "XbrlTableCode", "ConceptID", "YDimVal", "ZDimVal", "JsonBlob",
            ]))
        {
            foreach (var candidate in tableCandidates)
            {
                var mTableId = nextTableId++;
                tableIdByTableVId[candidate.TableVId] = mTableId;

                if (candidate.Tv.ConceptId.HasValue)
                {
                    referencedConceptIds.Add(candidate.Tv.ConceptId.Value);
                    translationSeeds.Add((candidate.Tv.ConceptId.Value, candidate.Label, null));
                }

                tableWriter.AddRow(
                    mTableId,
                    candidate.Tv.XbrlTableCode, // measured: mTable.TableCode comes from TableVersion.XbrlTableCode, not TableVersionCode
                    candidate.Label, // measured: XbrlTableCode + ":" + TableVersionLabel, 151/151 = 100%
                    candidate.Tv.FromDate, // Access has it and the reference leaves it empty
                    candidate.Tv.ToDate,
                    candidate.Tv.XbrlFilingIndicatorCode,
                    null, // mTable.XbrlTableCode (the target column itself): "Not used", always NULL in the 3 references
                    candidate.Tv.ConceptId,
                    null, // YDimVal: requires the axes, out of scope for this loader
                    null, // ZDimVal: likewise
                    Array.Empty<byte>()); // empty BLOB, NOT NULL, like mModule.JsonBlob
            }
        }

        // ---- mTemplateOrTable BusinessTable: ONE node for EACH (TaxonomyID, TableVID), not one
        // per TableVID. The L2 parent is resolved with the TemplateID/TableGroupID of THAT
        // association, never with those of a foreign "primary" taxonomy. Grouped by L2 parent for
        // the alphabetical Order; since each L2 node belongs to a single taxonomy, grouping by
        // ParentTemplateOrTableID already separates the associations by taxonomy. ----
        var businessTableCandidates = new List<(int TaxonomyId, int TableVId, AccessTableVersionRow Tv, int ParentId, string Code, string Label)>();

        foreach (var row in selectedTaxonomyTableVersions.OrderBy(v => v.TaxonomyId).ThenBy(v => v.TableVId))
        {
            if (!tableVersionsById.TryGetValue(row.TableVId, out var tv))
            {
                throw new InvalidOperationException(
                    $"TaxonomyTableVersion references TableVID={row.TableVId}, which is missing from TableVersion.");
            }

            var parentId = row.TemplateId.HasValue && row.TableGroupId.HasValue
                && l2NodeIdByTriple.TryGetValue(
                    (row.TaxonomyId, row.TemplateId.Value, row.TableGroupId.Value), out var l2Id)
                ? l2Id
                : 0; // 0 if it does not resolve (orphan)

            var code = tv.XbrlTableCode!; // already validated above, in tableCandidates
            var label = code + ":" + tv.TableVersionLabel;

            businessTableCandidates.Add((row.TaxonomyId, row.TableVId, tv, parentId, code, label));
        }

        var businessTableNodeIdByTaxonomyAndTableVId = new Dictionary<(int TaxonomyId, int TableVId), int>();

        foreach (var parentGroup in businessTableCandidates.GroupBy(c => c.ParentId).OrderBy(g => g.Key))
        {
            var siblingsOrdered = parentGroup.OrderBy(c => c.Code, StringComparer.Ordinal).ToList();

            for (var order = 0; order < siblingsOrdered.Count; order++)
            {
                var candidate = siblingsOrdered[order];
                var nodeId = nextTemplateOrTableId++;
                businessTableNodeIdByTaxonomyAndTableVId[(candidate.TaxonomyId, candidate.TableVId)] = nodeId;

                // ConceptID = TableVersion.ConceptID, the SAME concept as the mTable row of this
                // TableVID (above) and as ANY OTHER BusinessTable node of the same TableVID in
                // another taxonomy: sharing a ConceptID between rows that describe the same
                // business object is accepted and declared, not a synthesis.
                // referencedConceptIds already has it registered from the mTable loop, so it does
                // not need to be repeated here.
                templateOrTableWriter.AddRow(
                    nodeId,
                    candidate.TaxonomyId,
                    candidate.Code,
                    candidate.Label, // same rule as mTable.TableLabel
                    "BusinessTable",
                    order,
                    1, // Level is per type, not depth; BusinessTable is always 1 in 4.0/4.2
                    candidate.ParentId,
                    candidate.Tv.ConceptId, // TableVersion.ConceptID, shared
                    true,
                    null, null, null, null, null, null);
            }
        }

        // As before opening tableWriter: flush the pending batch of templateOrTableWriter
        // (the BusinessTable rows) before opening the mTaxonomyTable writer.
        templateOrTableWriter.Flush();

        // ---- mTaxonomyTable: one row per (TaxonomyID, TableVID). TableID is deduplicated
        // (mTable); AnnotatedTableID is NOT shared between taxonomies: it points to the
        // BusinessTable node of that taxonomy itself. ----
        var taxonomyTableRows = 0;
        using (var taxonomyTableWriter = new SqliteBatchWriter(
            destination,
            "mTaxonomyTable",
            ["TaxonomyID", "TableID", "AnnotatedTableID", "IsSimplyReuse", "IsTableSource"]))
        {
            foreach (var row in selectedTaxonomyTableVersions.OrderBy(v => v.TaxonomyId).ThenBy(v => v.TableVId))
            {
                if (!tableIdByTableVId.TryGetValue(row.TableVId, out var tableId)
                    || !businessTableNodeIdByTaxonomyAndTableVId.TryGetValue((row.TaxonomyId, row.TableVId), out var annotatedTableId))
                {
                    // Should not happen: selectedTaxonomyTableVersions is exactly the source of
                    // tableVersionIds/businessTableCandidates above.
                    throw new InvalidOperationException(
                        $"TaxonomyTableVersion (TaxonomyID={row.TaxonomyId}, TableVID={row.TableVId}) "
                        + "has no resolved mTable/mTemplateOrTable.");
                }

                taxonomyTableWriter.AddRow(
                    row.TaxonomyId,
                    tableId,
                    annotatedTableId,
                    true, // IsSimplyReuse: constant 1 (0 in 3.2, 1 in 4.0/4.2; 4.0/4.2 win). NOT TaxonomyTableVersion.IsSimpleReuse verbatim
                    true); // IsTableSource: no source in Access, measured constant 1 (see the type comment)

                taxonomyTableRows++;
            }
        }

        return (tableCandidates.Count, businessTableNodeIdByTaxonomyAndTableVId.Count, taxonomyTableRows, tableIdByTableVId);
    }

    // ------------------------------------------------------------------
    // Normalisation of the "eba_tg..." code: the 3.2 reference wins (and where it does not
    // reach, 4.0). The short hyphen '-' (U+002D) is KEPT; the long dash '–' (U+2013, and the em
    // dash '—', U+2014, in case they appear) is REMOVED. Spaces become '_'.
    //
    // internal (not private): ModuleLoader reproduces the same L1 node code to resolve the
    // BusinessTemplateID of mModuleBusinessTemplate from Access.TableGroupCode, without
    // duplicating this logic.
    // ------------------------------------------------------------------

    internal static string NormalizeCode(string code)
    {
        var buffer = new char[code.Length];
        var length = 0;

        foreach (var ch in code)
        {
            if (char.IsWhiteSpace(ch))
            {
                buffer[length++] = '_';
            }
            else if (ch is '–' or '—')
            {
                // Long dash (en dash / em dash): removed, not replaced by anything.
            }
            else
            {
                buffer[length++] = ch;
            }
        }

        return new string(buffer, 0, length);
    }
}
