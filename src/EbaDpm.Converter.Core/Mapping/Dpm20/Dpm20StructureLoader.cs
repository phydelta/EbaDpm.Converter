using System.Text.RegularExpressions;
using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Mapping.Dpm20;

/// <summary>
/// Loads the DPM 2.0 structure: <c>mTable</c>, <c>mTaxonomyTable</c> and
/// <c>mTemplateOrTable</c>. It runs after <see cref="Dpm20DictionaryLoader.Load"/>, on the same
/// connection.
///
/// This is a NEW pipeline, PARALLEL to the DPM 1.0
/// <see cref="EbaDpm.Converter.Core.Mapping.TemplateOrTableLoader"/>: it reuses only
/// <see cref="TemplateOrTableLoader.NormalizeCode"/> (the same treatment of the <c>eba_tg…</c>
/// code, with no variation between the two pipelines) — the remaining business rules are specific
/// to DPM 2.0.
///
/// <b>ROWS, not codes</b>: the five derivations are measured by distinct code (109/563/788), but
/// what is emitted are ROWS — one node for each <c>(TaxonomyID, …)</c> in which that code takes
/// part: 111/572/847 (846 before the abstract-table closure rule of
/// <see cref="Dpm20TaxonomyDeriver"/>, which adds the <c>C_34.02.b</c> pair with
/// <c>IF_CLASS2</c>).
///
/// <b>The ID design, deliberate.</b> <c>mTable</c> here is NOT deduplicated by <c>TableVID</c>
/// (unlike DPM 1.0): there is one row for each of the 847 (table, taxonomy) pairs already
/// computed by <see cref="Dpm20TaxonomyDeriver"/>. And, as measured:
/// <c>mTaxonomyTable.AnnotatedTableID = TableID</c> in all 847 rows, without a single exception.
/// The simplest way to reproduce that equality without inventing an id — and the one implemented
/// here — is for the SAME synthetic id to identify both the <c>mTable</c> row and the
/// <c>BusinessTable</c> node of <c>mTemplateOrTable</c> that describes that same pair: it is
/// literally the invariant "<c>mTaxonomyTable</c> and the <c>BusinessTable</c> rows are the SAME
/// set of pairs, 847 = 847" carried over to the id space. The <c>TableGroup</c> nodes (group and
/// template) use a disjoint id range, above the last pair.
///
/// <b>The template tree is built PER TAXONOMY.</b> Only the taxonomies whose release is the
/// cutoff one (4.2) have a tree: a group or template that is the parent of tables of two different
/// frameworks (two different 4.2 taxonomies) produces TWO nodes, one per taxonomy — that is how
/// 111/109 and 572/563 come out without a special case. Tables whose pair falls in an old-release
/// taxonomy have no tree: <c>ParentTemplateOrTableID = 0</c> (the root, and here also the orphan,
/// use 0, never NULL).
/// </summary>
public static class Dpm20StructureLoader
{
    /// <summary>Count of rows written, for the CLI report.</summary>
    /// <param name="TableVIdByTableId">
    /// The source <c>TableVID</c> of each of the 847 pairs emitted in <c>mTable.TableID</c> (the
    /// synthetic key of the pair). The same <c>TableVID</c> can appear for several distinct
    /// <c>TableID</c> (58 of the 847 pairs repeat a table under another taxonomy): that is why
    /// the mapping goes in THIS direction (TableID → TableVID, unique key) and not the other way
    /// round.
    /// </param>
    /// <param name="EmittedGroupIds">
    /// The 109 source <c>TableGroupID</c> that ARE emitted (some emitted table, and not contained
    /// in another group). <see cref="Dpm20ModuleLoader"/> reuses it as is for
    /// <c>mModuleBusinessTemplate</c> — the computation lives HERE, once.
    /// </param>
    /// <param name="GroupTableIdsById">
    /// The tables (<c>TableID</c>, NOT filtered by emitted) of each current <c>TableGroupID</c>,
    /// via <c>TableGroupComposition</c>. Same source used by <see cref="ResolveTemplateNode"/> to
    /// resolve the parent of a template.
    /// </param>
    public sealed record Result(
        int TableRows,
        int TaxonomyTableRows,
        int TemplateOrTableRows,
        int TableGroupLevel1Rows,
        int TemplateLevel2Rows,
        int BusinessTableRows,
        bool PairInvariantHolds,
        IReadOnlyList<string> UnresolvedCases,
        IReadOnlyDictionary<int, int> TableVIdByTableId,
        IReadOnlySet<int> EmittedGroupIds,
        IReadOnlyDictionary<int, IReadOnlySet<int>> GroupTableIdsById);

    /// <summary><c>mTemplateOrTable</c> node under construction: the <see cref="Order"/> is resolved at the end, grouping by <see cref="ParentId"/>.</summary>
    private sealed class NodeDraft
    {
        public required int Id { get; init; }
        public required int TaxonomyId { get; init; }
        public required string Code { get; init; }
        public required string? Label { get; init; }
        public required string Type { get; init; }
        public required int Level { get; init; }
        public required int ParentId { get; init; }
        /// <summary>The code WITHOUT the <c>eba_tg</c> prefix: the basis of the natural order.</summary>
        public required string SortKey { get; init; }
        public int Order { get; set; }
    }

    private readonly record struct L1Key(int TaxonomyId, int TableGroupId);

    private readonly record struct L2Key(int TaxonomyId, int TemplateTableId);

    public static Result Load(
        Dpm20AccessReader reader,
        SqliteConnection destination,
        Dpm20TaxonomyDerivationResult derivation,
        IReadOnlyList<Dpm20TableVersionRow> currentTableVersions,
        IReadOnlyList<AccessTaxonomyRow> selectedTaxonomies)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(derivation);
        ArgumentNullException.ThrowIfNull(currentTableVersions);
        ArgumentNullException.ThrowIfNull(selectedTaxonomies);

        var unresolvedCases = new List<string>();
        var cutoffReleaseCode = reader.CutoffReleaseCode;

        var tableVersionByTableVId = currentTableVersions.ToDictionary(tv => tv.TableVId);
        var tableVersionByTableId = BuildTableVersionByTableId(currentTableVersions, unresolvedCases);

        // Concrete children of each abstract TableID (second half of the parent rule):
        // TableGroupComposition never points to an abstract table, so when the template is
        // abstract, the group has to be looked up through its children.
        var childrenTableIdsByAbstractTableId = new Dictionary<int, List<int>>();
        foreach (var tv in currentTableVersions)
        {
            if (tv.AbstractTableId is not { } abstractTableId)
            {
                continue;
            }

            if (!childrenTableIdsByAbstractTableId.TryGetValue(abstractTableId, out var list))
            {
                list = [];
                childrenTableIdsByAbstractTableId[abstractTableId] = list;
            }

            list.Add(tv.TableId);
        }

        // ---- The source groups: 122 current, all Type=templateGroup ----
        var tableGroups = reader.ReadTableGroups()
            .Where(g => string.Equals(g.Type, "templateGroup", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(g => g.TableGroupId);

        var groupTableIdsById = new Dictionary<int, HashSet<int>>();
        foreach (var composition in reader.ReadTableGroupCompositions())
        {
            if (!tableGroups.ContainsKey(composition.TableGroupId))
            {
                continue; // Group not current, or of a Type other than templateGroup.
            }

            if (!groupTableIdsById.TryGetValue(composition.TableGroupId, out var set))
            {
                set = [];
                groupTableIdsById[composition.TableGroupId] = set;
            }

            set.Add(composition.TableId);
        }

        // ---- Which groups are emitted. GLOBAL, over the COMPLETE universe of 788 emitted codes
        // (the 32 derived taxonomies, not only those selected in this run): it is a structural
        // property of the source against the destination, not of the user's selection. The set of
        // 109 groups is NOT recomputed per selection — it is ALWAYS computed over the complete
        // universe, and what varies with the selection is only which L1/L2 nodes end up being
        // materialized (further below, in the `pairs` loop: a group among the 109 only gets a node
        // if some table of the current SELECTION falls under it). That way the tree of a taxonomy
        // does not change shape depending on which others it is converted with: the structure is
        // the explicit part, but its definition does not depend on the filter. ----
        var globalEmittedTableIds = derivation.TableVIdsByTaxonomyId.Values
            .SelectMany(v => v)
            .Distinct()
            .Where(tableVersionByTableVId.ContainsKey)
            .Select(vid => tableVersionByTableVId[vid].TableId)
            .ToHashSet();

        var groupEmittedTablesById = groupTableIdsById
            .Select(kvp => (GroupId: kvp.Key, Tables: kvp.Value.Intersect(globalEmittedTableIds).ToHashSet()))
            .Where(x => x.Tables.Count > 0)
            .ToDictionary(x => x.GroupId, x => x.Tables);

        var emittedGroupIds = groupEmittedTablesById.Keys
            .Where(g => !IsSubsumedByAnotherGroup(g, groupEmittedTablesById))
            .ToHashSet();

        // ---- The 847 selected (TaxonomyID, TableVID) pairs, in deterministic order: the same
        // synthetic id serves as mTable.TableID AND as TemplateOrTableID of the BusinessTable node. ----
        var selectedTaxonomyIds = selectedTaxonomies.Select(t => t.TaxonomyId).ToHashSet();
        var pairs = new List<(int TaxonomyId, int TableVId)>();
        foreach (var taxonomyId in selectedTaxonomyIds.OrderBy(id => id))
        {
            if (!derivation.TableVIdsByTaxonomyId.TryGetValue(taxonomyId, out var tableVIds))
            {
                continue;
            }

            foreach (var tableVId in tableVIds) // already sorted ascending by the deriver
            {
                pairs.Add((taxonomyId, tableVId));
            }
        }

        // The template tree only exists in the taxonomies of the cutoff release.
        var treeTaxonomyIds = selectedTaxonomies
            .Where(t => string.Equals(t.DpmPackageCode, cutoffReleaseCode, StringComparison.Ordinal))
            .Select(t => t.TaxonomyId)
            .ToHashSet();

        var l1Nodes = new List<NodeDraft>();
        var l1IdByKey = new Dictionary<L1Key, int>();
        var l2Nodes = new List<NodeDraft>();
        var l2IdByKey = new Dictionary<L2Key, int>();
        var nextGroupNodeId = pairs.Count + 1; // 1..pairs.Count already reserved for the pairs.

        var businessTableNodes = new List<NodeDraft>(pairs.Count);
        var mTableRows = new List<(int TableId, string Code, string Label, string XbrlFilingIndicatorCode)>(pairs.Count);
        var taxonomyTableRows = new List<(int TaxonomyId, int TableId)>(pairs.Count);

        for (var i = 0; i < pairs.Count; i++)
        {
            var pairId = i + 1;
            var (taxonomyId, tableVId) = pairs[i];

            if (!tableVersionByTableVId.TryGetValue(tableVId, out var tv))
            {
                throw new InvalidOperationException(
                    $"Dpm20TaxonomyDeriver references TableVID={tableVId} (TaxonomyID={taxonomyId}), "
                    + "which is missing from the current TableVersion.");
            }

            var code = tv.Code;
            var label = code + ":" + tv.Name; // NOT trimmed.

            var (templateTableId, templateCode, templateName) = ResolveTemplate(tv, tableVersionByTableId, unresolvedCases);

            var parentId = 0;
            if (treeTaxonomyIds.Contains(taxonomyId))
            {
                parentId = ResolveTemplateNode(
                    taxonomyId, templateTableId, templateCode, templateName,
                    tableGroups, groupTableIdsById, childrenTableIdsByAbstractTableId, emittedGroupIds,
                    l1IdByKey, l1Nodes, l2IdByKey, l2Nodes, ref nextGroupNodeId, unresolvedCases);
            }

            mTableRows.Add((pairId, code, label, templateCode));
            taxonomyTableRows.Add((taxonomyId, pairId));

            businessTableNodes.Add(new NodeDraft
            {
                Id = pairId,
                TaxonomyId = taxonomyId,
                Code = code, // BusinessTables do NOT carry eba_tg: their code IS the TableCode.
                Label = label,
                Type = "BusinessTable",
                Level = 1, // Level is a classification, not a depth.
                ParentId = parentId,
                SortKey = code,
            });
        }

        // ---- Order: 0-based rank by NATURAL order of the code, within the parent.
        // The L1 groups were already created with a constant Order=0 (ResolveTemplateNode).
        //
        // For the 48 orphan BusinessTables (Parent=0, old-release taxonomies) this is a DECLARED
        // CHOICE, NOT DERIVED from the source — measured, not assumed: in the reference those 47
        // rows carry Order = 0..21 consecutively, and then isolated jumps (291-295, 367-370,
        // 424-425, 600-602, 628-634, 794-797). It is NOT 0-based per parent or per taxonomy: esg
        // 4.0 has exactly 3 orphan rows and their Order values are 632/633/634, not 0/1/2. Two more
        // readings were tried — global rank among the 788 codes, and the Order the same table has
        // in its own 4.2 taxonomy — and they match 0/47 and 3/47. The reference seems to number them
        // from an internal counter of its generator, not reproducible from the source. The SAME
        // 0-based rule by natural order as the rest is kept here (for consistency and determinism,
        // not because there is evidence that the reference does so): it is ordering metadata of 48
        // rows of old taxonomies, it does not block, and it stays as a declared divergence. ----
        AssignNaturalOrder(l2Nodes);
        AssignNaturalOrder(businessTableNodes);

        // ---- Writing: mTemplateOrTable, then mTable, then mTaxonomyTable ----
        using (var templateOrTableWriter = new SqliteBatchWriter(
            destination,
            "mTemplateOrTable",
            [
                "TemplateOrTableID", "TaxonomyID", "TemplateOrTableCode", "TemplateOrTableLabel",
                "TemplateOrTableType", "Order", "Level", "ParentTemplateOrTableID", "ConceptID",
                "IsTableGroupSource", "TC", "TT", "TL", "TD", "YC", "XC",
            ]))
        {
            foreach (var node in l1Nodes.Concat(l2Nodes).Concat(businessTableNodes))
            {
                templateOrTableWriter.AddRow(
                    node.Id,
                    node.TaxonomyId,
                    node.Code,
                    node.Label,
                    node.Type,
                    node.Order,
                    node.Level,
                    node.ParentId,
                    null, // ConceptID: NULL until the concept loader runs
                    true, // IsTableGroupSource: constant 1 in all 1,529 rows
                    null, null, null, null, null, null); // TC/TT/TL/TD/YC/XC: NULL
            }
        }

        using (var tableWriter = new SqliteBatchWriter(
            destination,
            "mTable",
            [
                "TableID", "TableCode", "TableLabel", "FromDate", "ToDate", "XbrlFilingIndicatorCode",
                "XbrlTableCode", "ConceptID", "YDimVal", "ZDimVal", "JsonBlob",
            ]))
        {
            foreach (var row in mTableRows)
            {
                tableWriter.AddRow(
                    row.TableId,
                    row.Code,
                    row.Label,
                    null, // FromDate: NULL
                    null, // ToDate: NULL
                    row.XbrlFilingIndicatorCode, // code of the TEMPLATE, without eba_tg
                    null, // mTable.XbrlTableCode (the destination's own column): NULL
                    null, // ConceptID: NULL until the concept loader runs
                    null, // YDimVal: NULL
                    null, // ZDimVal: NULL
                    Array.Empty<byte>()); // JsonBlob: empty BLOB, NOT NULL
            }
        }

        using (var taxonomyTableWriter = new SqliteBatchWriter(
            destination,
            "mTaxonomyTable",
            ["TaxonomyID", "TableID", "AnnotatedTableID", "IsSimplyReuse", "IsTableSource"]))
        {
            foreach (var row in taxonomyTableRows)
            {
                taxonomyTableWriter.AddRow(
                    row.TaxonomyId,
                    row.TableId,
                    row.TableId, // AnnotatedTableID = TableID (0 rows differ)
                    true, // IsSimplyReuse: constant 1
                    true); // IsTableSource: constant 1
            }
        }

        // The pair invariant, genuinely verified (not merely assumed by construction):
        // mTaxonomyTable and the BusinessTable rows of mTemplateOrTable are the SAME set of
        // (TaxonomyID, TableID) pairs.
        var taxonomyTablePairs = taxonomyTableRows.Select(r => (r.TaxonomyId, r.TableId)).ToHashSet();
        var businessTablePairs = businessTableNodes.Select(n => (n.TaxonomyId, n.Id)).ToHashSet();
        var invariantHolds = taxonomyTablePairs.SetEquals(businessTablePairs)
            && taxonomyTablePairs.Count == taxonomyTableRows.Count
            && businessTablePairs.Count == businessTableNodes.Count;

        // TableID (the pairId, 1..pairs.Count) -> source TableVID, in the SAME order in which
        // `pairs` was built (pairId = i + 1).
        var tableVIdByTableId = new Dictionary<int, int>(pairs.Count);
        for (var i = 0; i < pairs.Count; i++)
        {
            tableVIdByTableId[i + 1] = pairs[i].TableVId;
        }

        return new Result(
            TableRows: mTableRows.Count,
            TaxonomyTableRows: taxonomyTableRows.Count,
            TemplateOrTableRows: l1Nodes.Count + l2Nodes.Count + businessTableNodes.Count,
            TableGroupLevel1Rows: l1Nodes.Count,
            TemplateLevel2Rows: l2Nodes.Count,
            BusinessTableRows: businessTableNodes.Count,
            PairInvariantHolds: invariantHolds,
            UnresolvedCases: unresolvedCases,
            TableVIdByTableId: tableVIdByTableId,
            EmittedGroupIds: emittedGroupIds,
            GroupTableIdsById: groupTableIdsById.ToDictionary(
                kvp => kvp.Key, kvp => (IReadOnlySet<int>)kvp.Value));
    }

    // ------------------------------------------------------------------
    // The template of a table: TableVersion.AbstractTableID, or the table itself.
    // ------------------------------------------------------------------

    private static (int TemplateTableId, string TemplateCode, string? TemplateName) ResolveTemplate(
        Dpm20TableVersionRow tv,
        IReadOnlyDictionary<int, Dpm20TableVersionRow> tableVersionByTableId,
        List<string> unresolvedCases)
    {
        if (tv.AbstractTableId is not { } abstractTableId)
        {
            return (tv.TableId, tv.Code, tv.Name);
        }

        if (!tableVersionByTableId.TryGetValue(abstractTableId, out var abstractTv))
        {
            unresolvedCases.Add(
                $"{tv.Code} (TableVID={tv.TableVId}): AbstractTableID={abstractTableId} has no current "
                + "version in TableVersion; the table is used as its own template.");
            return (tv.TableId, tv.Code, tv.Name);
        }

        return (abstractTv.TableId, abstractTv.Code, abstractTv.Name);
    }

    // ------------------------------------------------------------------
    // The parent of a template: a TableGroup that contains it, looking through its concrete
    // children if the template is abstract. Memoizes the L1/L2 nodes already created for
    // (TaxonomyID, group/template): the same group or template can have a node in more than one
    // 4.2 taxonomy (2 of 109 groups, 9 of 563 templates), but never two nodes in the SAME one.
    // ------------------------------------------------------------------

    private static int ResolveTemplateNode(
        int taxonomyId,
        int templateTableId,
        string templateCode,
        string? templateName,
        IReadOnlyDictionary<int, Dpm20TableGroupRow> tableGroups,
        IReadOnlyDictionary<int, HashSet<int>> groupTableIdsById,
        IReadOnlyDictionary<int, List<int>> childrenTableIdsByAbstractTableId,
        HashSet<int> emittedGroupIds,
        Dictionary<L1Key, int> l1IdByKey,
        List<NodeDraft> l1Nodes,
        Dictionary<L2Key, int> l2IdByKey,
        List<NodeDraft> l2Nodes,
        ref int nextGroupNodeId,
        List<string> unresolvedCases)
    {
        var l2Key = new L2Key(taxonomyId, templateTableId);
        if (l2IdByKey.TryGetValue(l2Key, out var existingL2Id))
        {
            return existingL2Id;
        }

        var candidateTableIds = new List<int> { templateTableId };
        if (childrenTableIdsByAbstractTableId.TryGetValue(templateTableId, out var children))
        {
            candidateTableIds.AddRange(children);
        }

        // The group contains the template, or some of its concrete children — 563/563 only with
        // the union of the two. The search is among the EMITTED groups (109 of 122): a template
        // subsumed by another (e.g. Market_Risk) reappears with the same content under the group
        // that subsumes it, so restricting here loses no measured match.
        var matchingGroupIds = emittedGroupIds
            .Where(gid => groupTableIdsById.TryGetValue(gid, out var tables)
                && candidateTableIds.Exists(tables.Contains))
            .OrderBy(id => id)
            .ToList();

        int groupNodeParentId;
        if (matchingGroupIds.Count == 0)
        {
            unresolvedCases.Add(
                $"Template TableID={templateTableId} ('{templateCode}') in TaxonomyID={taxonomyId}: "
                + "no emitted TableGroup contains it (neither it nor its concrete children).");
            groupNodeParentId = 0;
        }
        else
        {
            if (matchingGroupIds.Count > 1)
            {
                unresolvedCases.Add(
                    $"Template TableID={templateTableId} ('{templateCode}') in TaxonomyID={taxonomyId}: "
                    + $"{matchingGroupIds.Count} candidate groups ({string.Join(", ", matchingGroupIds)}); "
                    + "the one with the lowest TableGroupID is used.");
            }

            var groupId = matchingGroupIds[0];
            var l1Key = new L1Key(taxonomyId, groupId);
            if (!l1IdByKey.TryGetValue(l1Key, out var l1Id))
            {
                l1Id = nextGroupNodeId++;
                l1IdByKey[l1Key] = l1Id;

                var group = tableGroups[groupId];
                l1Nodes.Add(new NodeDraft
                {
                    Id = l1Id,
                    TaxonomyId = taxonomyId,
                    Code = "eba_tg" + TemplateOrTableLoader.NormalizeCode(group.Code),
                    Label = group.Name,
                    Type = "TableGroup",
                    Level = 1,
                    ParentId = 0, // root: 0, not NULL
                    SortKey = group.Code,
                    Order = 0, // constant at L1
                });
            }

            groupNodeParentId = l1Id;
        }

        var l2Id = nextGroupNodeId++;
        l2IdByKey[l2Key] = l2Id;

        l2Nodes.Add(new NodeDraft
        {
            Id = l2Id,
            TaxonomyId = taxonomyId,
            Code = "eba_tg" + TemplateOrTableLoader.NormalizeCode(templateCode),
            Label = templateName,
            Type = "TableGroup",
            Level = 2,
            ParentId = groupNodeParentId,
            SortKey = templateCode,
        });

        return l2Id;
    }

    // ------------------------------------------------------------------
    // A group is emitted if it has some emitted table and its set of emitted tables is not
    // contained in that of another group. Tie (identical sets): the lowest TableGroupID wins, so
    // that the relation is deterministic and the two are never eliminated at once.
    // ------------------------------------------------------------------

    private static bool IsSubsumedByAnotherGroup(int groupId, IReadOnlyDictionary<int, HashSet<int>> tablesByGroupId)
    {
        var tables = tablesByGroupId[groupId];

        foreach (var (otherGroupId, otherTables) in tablesByGroupId)
        {
            if (otherGroupId == groupId || !tables.IsSubsetOf(otherTables))
            {
                continue;
            }

            if (otherTables.Count > tables.Count)
            {
                return true;
            }

            if (otherTables.Count == tables.Count && otherGroupId < groupId)
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Order: 0-based rank, NATURAL order of the code, within the parent.
    // ------------------------------------------------------------------

    private static void AssignNaturalOrder(List<NodeDraft> nodes)
    {
        foreach (var group in nodes.GroupBy(n => n.ParentId))
        {
            var siblings = group.OrderBy(n => n.SortKey, NaturalStringComparer.Instance).ToList();
            for (var order = 0; order < siblings.Count; order++)
            {
                siblings[order].Order = order;
            }
        }
    }

    private static Dictionary<int, Dpm20TableVersionRow> BuildTableVersionByTableId(
        IReadOnlyList<Dpm20TableVersionRow> currentTableVersions, List<string> unresolvedCases)
    {
        var result = new Dictionary<int, Dpm20TableVersionRow>();
        foreach (var tv in currentTableVersions)
        {
            if (!result.TryAdd(tv.TableId, tv))
            {
                unresolvedCases.Add(
                    $"TableID={tv.TableId}: more than one TableVersion current at the same time "
                    + $"(TableVID={tv.TableVId} and TableVID={result[tv.TableId].TableVId}); "
                    + "0 collisions are assumed — the first one read is kept.");
            }
        }

        return result;
    }

    /// <summary>
    /// Natural order (digits are compared as a number, not character by character): <c>C_2</c>
    /// before <c>C_10</c>. The source <c>Order</c> (<c>TableGroupComposition.Order</c>) does NOT
    /// work (9/572); the destination renumbers with this criterion.
    ///
    /// <c>internal</c> (not <c>private</c>): <see cref="Dpm20ModuleLoader"/> reuses the SAME
    /// criterion for the <c>Order</c> of <c>mModuleBusinessTemplate</c> — a declared choice for
    /// internal consistency, not a derivation.
    /// </summary>
    internal sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new();

        private static readonly Regex SplitPattern = new(@"(\d+)", RegexOptions.Compiled);

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var partsX = SplitPattern.Split(x);
            var partsY = SplitPattern.Split(y);
            var len = Math.Min(partsX.Length, partsY.Length);

            for (var i = 0; i < len; i++)
            {
                var px = partsX[i];
                var py = partsY[i];
                int cmp;

                if (px.Length > 0 && py.Length > 0 && char.IsDigit(px[0]) && char.IsDigit(py[0]))
                {
                    var trimmedX = px.TrimStart('0');
                    var trimmedY = py.TrimStart('0');
                    cmp = trimmedX.Length != trimmedY.Length
                        ? trimmedX.Length.CompareTo(trimmedY.Length)
                        : string.CompareOrdinal(trimmedX, trimmedY);

                    if (cmp == 0)
                    {
                        cmp = string.CompareOrdinal(px, py); // tie-break by leading zeros
                    }
                }
                else
                {
                    cmp = string.CompareOrdinal(px, py);
                }

                if (cmp != 0)
                {
                    return cmp;
                }
            }

            return partsX.Length.CompareTo(partsY.Length);
        }
    }
}
