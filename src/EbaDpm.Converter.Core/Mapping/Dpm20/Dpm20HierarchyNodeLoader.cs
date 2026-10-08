using System.Globalization;
using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Mapping.Dpm20;

/// <summary>
/// Loads <c>mHierarchyNode</c>: the <c>SubCategoryItem</c> tree of the EFFECTIVE version of each
/// subcategory — the current one if there is one, and the LAST one if there is not (53
/// subcategories).
///
/// This is a NEW, SEPARATE FILE from <see cref="Dpm20DictionaryLoader"/> on purpose: it is a
/// closed task with its own specification and its own source (<c>SubCategoryItem</c>,
/// <c>Operator</c> — neither is read by the dictionary loader), and
/// <see cref="Dpm20DictionaryLoader"/> already covers a complete and large scope on its own. It
/// runs AFTER <see cref="Dpm20DictionaryLoader.Load"/>, on the same connection: it needs
/// <c>mDomain</c> and <c>mMember</c> already written (they are re-read from
/// <paramref name="destination"/>, not recomputed) and
/// <see cref="Dpm20DictionaryLoader.Result.HierarchyBySubCategoryId"/> (the 1:1
/// <c>SubCategoryID → HierarchyID</c> correspondence, already computed).
///
/// <b>The columns, measured over 13,440 comparable nodes:</b>
/// <list type="bullet">
/// <item><description><c>HierarchyID</c>: the hierarchy of the subcategory (1:1).</description></item>
/// <item><description>
/// <c>MemberID</c>: the <c>ItemID</c> of the node, resolved to its EFFECTIVE <c>ItemCategory</c>
/// row. <b>Measured finding</b>: the resolution is NOT restricted to the category (domain) of the
/// containing subcategory. 68 subcategories (<c>new_IG1</c>, <c>new_qSE_agg_*</c>… 1,084 of 16,538
/// rows) are hierarchies over a UNION domain (the union is not materialized) whose items live in
/// <c>ItemCategory</c> under the category of a COMPONENT domain, not under their own — e.g.
/// <c>new_IG1</c> hangs from <c>qIG</c> but its items are in <c>ItemCategory</c> with the
/// <c>CategoryID</c> of <c>GA</c>. Restricting by the category of the subcategory leaves those
/// 1,084 rows unresolved; resolving by the item's OWN category (measured: 0 of 16,538 rows
/// unresolved, 0 ambiguous — not a single time does an item have more than one current row in the
/// WHOLE of <c>ItemCategory</c>, in any category) resolves them all without ambiguity. 5 cases
/// (<c>EC</c>↔<c>qEC</c>) are the same domain-migration phenomenon already documented for
/// <c>mDimension</c>, seen now from <c>mHierarchyNode</c>.
///
/// "Current" is NOT "current at the cutoff" — it is current IN THE RELEASE OF THE SUBCATEGORY
/// VERSION BEING EMITTED (the cutoff for the 1,101 current ones, that of their last version for the
/// 53 that fall back to the last version). The same <c>ItemID</c> changes category between
/// releases (measured: <c>ItemID=1012404560</c> is <c>qSR:qx2009</c> in <c>[3,5)</c> and
/// <c>qTE:qx2066</c> from release 5), so always resolving it at the cutoff mixed the node of a
/// <c>qSR</c> hierarchy with a <c>qTE</c> member when the hierarchy being emitted was the
/// fallback one (its last version, earlier than the cutoff). The search is still GLOBAL across
/// categories — the union domain above requires it —; what changes is the release at which
/// "current" is evaluated. Measured, it wins on all four figures at once: exact hierarchies
/// 952→959, without containment 15→8, nodes with a domain not joined to that of their hierarchy
/// 3→0, rows 16,538 (unchanged).
/// </description></item>
/// <item><description>
/// <c>ParentMemberID</c>: the same criterion applied to <c>ParentItemID</c> — 13,439 of 13,440
/// against the reference. A <c>ParentItemID</c> that is not a node of the SAME subcategory stops
/// the conversion: today 0 cases, measured.
/// </description></item>
/// <item><description>
/// <c>ComparisonOperator</c>/<c>UnaryOperator</c>: <c>Operator.Symbol</c> of
/// <c>ComparisonOperatorID</c>/<c>ArithmeticOperatorID</c>. <c>UnaryOperator</c> is an empty
/// string (NOT NULL) when there is no operator — measured 29,107/31,004 in the reference and 0
/// NULL.
/// </description></item>
/// <item><description><c>IsAbstract</c>: constant 0 (31,004/31,004 in the reference).</description></item>
/// <item><description>
/// <c>HierarchyNodeLabel</c>: <c>Item.Name</c> of the item — NEVER <c>SubCategoryItem.Label</c>,
/// which is EMPTY in all 14,919 current rows (measured 31,004/31,004).
/// </description></item>
/// <item><description><c>Order</c>: <c>SubCategoryItem.Order</c> VERBATIM — the reference's order is not pursued.</description></item>
/// <item><description>
/// <c>Level</c>/<c>Path</c>: BUILT by climbing through <c>ParentItemID</c>, with the usual path
/// form (the dot as separator and terminator) — there is no source <c>Path</c> to normalize here,
/// it is built from scratch.
/// </description></item>
/// <item><description><c>HierarchyNodeID</c>: synthetic, as in DPM 1.0.</description></item>
/// <item><description><c>ConceptID</c>: NULL here — it is minted by <see cref="Dpm20ConceptLoader"/> in its final phase, after the skeleton is written.</description></item>
/// </list>
///
/// The 53 subcategories WITHOUT a current version take their LAST version (by maximum
/// <c>StartReleaseID</c>, measured with no ties); if that last version has no items either, the
/// hierarchy is left without nodes — measured, it is neither repaired nor filled in.
///
/// The <c>MemberID</c> resolution is GLOBAL across categories (above), and in 4.3 that leaves 137
/// nodes whose resolved member does NOT belong to the domain of their own hierarchy —
/// <c>ItemCategory</c> re-versions the item right at the release cutoff and the containing
/// subcategory does not follow it. The measurement splits the 137 in two: **33** (<c>GA30</c>)
/// have a TWIN member with the same code under the hierarchy's domain — <c>ResolveMemberId</c>
/// now ANCHORS there — and **104** (<c>GA4</c>, <c>GA4_1</c>, <c>MC150</c>) have no candidate
/// under their own domain: anchoring there would make the "no matching row in mMember" exception
/// throw and break the conversion outright, so they are emitted exactly as before and remain
/// declared as a named exception in <c>I-TRE-07b</c> — the source asserts both things at once and
/// it is not for us to arbitrate.
/// </summary>
public static class Dpm20HierarchyNodeLoader
{
    public sealed record Result(
        int HierarchyNodeRows,
        int HierarchiesWithoutNodes,
        IReadOnlyList<string> AnchoredMembers);

    public static Result Load(
        Dpm20AccessReader reader,
        SqliteConnection destination,
        IReadOnlyDictionary<int, (int HierarchyId, string HierarchyCode, int DomainId)> hierarchyBySubCategoryId)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(hierarchyBySubCategoryId);

        var cutoff = reader.CutoffReleaseId;

        // EFFECTIVE version (current, or the last one if there is none) of each subcategory,
        // TOGETHER WITH the release it represents: the cutoff for the 1,101 current ones, the
        // StartReleaseID of their own last version for the 53 without a current one. That release
        // is the time coordinate that is missing below.
        var effectiveVersionsBySubCategoryId = ResolveEffectiveSubCategoryVersions(reader.ReadAllSubCategoryVersions(), cutoff);
        var effectiveVidSet = new HashSet<int>(effectiveVersionsBySubCategoryId.Values.Select(v => v.SubCategoryVId));

        // The whole SubCategoryItem, grouped by EFFECTIVE SubCategoryVID only.
        var itemsByVid = new Dictionary<int, List<Dpm20SubCategoryItemRow>>();
        foreach (var item in reader.ReadSubCategoryItems())
        {
            if (!effectiveVidSet.Contains(item.SubCategoryVId))
            {
                continue;
            }

            if (!itemsByVid.TryGetValue(item.SubCategoryVId, out var list))
            {
                list = [];
                itemsByVid[item.SubCategoryVId] = list;
            }

            list.Add(item);
        }

        // All the ItemCategory rows of each ItemID, WITHOUT collapsing to a single "effective"
        // one — the same ItemID can resolve to a different category depending on the release asked
        // about (measured: ItemID=1012404560 is qSR:qx2009 in [3,5) and qTE:qx2066 from release
        // 5). The search is still GLOBAL across categories (the union domain requires it), but it
        // is now evaluated AT THE RELEASE of the subcategory version being emitted, not at the
        // fixed cutoff.
        var itemCategoryRowsByItemId = new Dictionary<int, List<Dpm20ItemCategoryRow>>();
        foreach (var row in reader.ReadItemCategories())
        {
            if (!itemCategoryRowsByItemId.TryGetValue(row.ItemId, out var list))
            {
                list = [];
                itemCategoryRowsByItemId[row.ItemId] = list;
            }

            list.Add(row);
        }

        var categoryCodeById = reader.ReadCategories().ToDictionary(c => c.CategoryId, c => c.Code);
        var domainIdByDomainCode = ReadDomainIdByDomainCode(destination);
        var domainCodeByDomainId = domainIdByDomainCode.ToDictionary(kv => kv.Value, kv => kv.Key);
        var memberIdByDomainAndCode = ReadMemberIdByDomainAndCode(destination, out var memberLabelById);
        var domainUnionPairs = ReadDomainUnionPairs(destination);
        var memberIdByItemIdReleaseAndHierarchyDomain = new Dictionary<(int ItemId, int Release, int HierarchyDomainId), int>();
        var anchoredMembers = new List<string>();

        int ResolveMemberId(int itemId, int release, int hierarchyDomainId, int hierarchyId)
        {
            var cacheKey = (itemId, release, hierarchyDomainId);
            if (memberIdByItemIdReleaseAndHierarchyDomain.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            if (!itemCategoryRowsByItemId.TryGetValue(itemId, out var rows) || rows.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Hierarchy nodes: ItemID={itemId} has no row in ItemCategory; its MemberID in "
                    + "mHierarchyNode cannot be resolved. Measured 0 such cases — if one appears, it is "
                    + "a new Access database with an uncategorized item.");
            }

            // Current AT THE RECEIVED RELEASE (not at the fixed cutoff), and if there is none the
            // one with the highest StartReleaseID — the same criterion as the effective subcategory
            // version, parametrized by release.
            var itemCategory = rows.FirstOrDefault(r => IsCurrent(r.StartReleaseId, r.EndReleaseId, release))
                ?? rows.OrderByDescending(r => r.StartReleaseId).First();

            var categoryCode = categoryCodeById.GetValueOrDefault(itemCategory.CategoryId);
            var domainCode = categoryCode == "_PR" ? "MET" : categoryCode;

            if (domainCode is null || !domainIdByDomainCode.TryGetValue(domainCode, out var domainId))
            {
                throw new InvalidOperationException(
                    $"Hierarchy nodes: ItemID={itemId} (CategoryID={itemCategory.CategoryId}, Code="
                    + $"'{categoryCode ?? "<unknown>"}') resolves to a domain ('{domainCode ?? "<null>"}') "
                    + "with no matching row in mDomain. Measured 0 such cases.");
            }

            // The NATURAL resolution (global, unanchored) is ALWAYS computed first, exactly as
            // before the anchoring was introduced — this guarantees that "with no safe anchor
            // candidate, the behaviour does not change by a single byte" does not depend on a new
            // branch, but on the OLD branch still being the first one to run.
            if (!memberIdByDomainAndCode.TryGetValue((domainId, itemCategory.Code), out var naturalMemberId))
            {
                throw new InvalidOperationException(
                    $"Hierarchy nodes: ItemID={itemId}: (DomainID={domainId}, MemberCode='{itemCategory.Code}') "
                    + "has no matching row in mMember. Measured 0 such cases.");
            }

            var memberId = naturalMemberId;

            // If the NATURAL domain is not that of the hierarchy containing the node, AND a member
            // with the SAME code exists under the hierarchy's domain, AND that member is the SAME
            // business twin —same MemberLabel, not a code collision with an unrelated member—, AND
            // the mismatch is NOT ALREADY explained by mDomainUnion (a union-domain hierarchy
            // referencing its components is DELIBERATE), the member of the hierarchy's own domain
            // is preferred. This is the GA30 case (GA:x57, MemberID 5998, "CY - Central Bank of
            // Cyprus") versus the qNT:x57 (5999, same label) that used to be resolved.
            //
            // Two conditions are NOT cosmetic, both measured on the whole of 4.3 BEFORE applying
            // them: (1) the MemberLabel — without it, 22 MORE nodes in 17 unrelated hierarchies
            // would have anchored to DISTINCT business members that merely share a generic code
            // reused across domains (e.g. "qx2006" is "Derivatives" in qFI and "Debt instruments..."
            // in qAF). (2) mDomainUnion — without it, HierarchyID=649 (qx2001, "Banking book")
            // would have anchored ALSO IN 4.2 (1 row, measured): its 91↔90 mismatch is DELIBERATE
            // (mDomainUnion already declares it), and "0 anchorings in 4.2" is the condition that
            // gives it away.
            if (hierarchyDomainId != domainId
                && memberIdByDomainAndCode.TryGetValue((hierarchyDomainId, itemCategory.Code), out var anchoredMemberId)
                && memberLabelById.GetValueOrDefault(anchoredMemberId) == memberLabelById.GetValueOrDefault(naturalMemberId)
                && !domainUnionPairs.Contains((hierarchyDomainId, domainId)))
            {
                memberId = anchoredMemberId;
                var discardedDomainCode = domainCodeByDomainId.GetValueOrDefault(domainId, domainId.ToString(CultureInfo.InvariantCulture));
                var chosenDomainCode = domainCodeByDomainId.GetValueOrDefault(hierarchyDomainId, hierarchyDomainId.ToString(CultureInfo.InvariantCulture));
                anchoredMembers.Add(
                    $"HierarchyID={hierarchyId}: ItemID={itemId} (code='{itemCategory.Code}') anchored to "
                    + $"DomainID={hierarchyDomainId} ('{chosenDomainCode}') instead of DomainID={domainId} "
                    + $"('{discardedDomainCode}').");
            }

            memberIdByItemIdReleaseAndHierarchyDomain[cacheKey] = memberId;
            return memberId;
        }

        // HierarchyNodeLabel comes from Item.Name, NEVER from SubCategoryItem.Label (empty).
        var itemNameById = reader.ReadItems().ToDictionary(i => i.ItemId, i => i.Name);
        var operatorSymbolById = reader.ReadOperators().ToDictionary(o => o.OperatorId, o => o.Symbol);

        using var writer = new SqliteBatchWriter(
            destination,
            "mHierarchyNode",
            [
                "HierarchyNodeID", "HierarchyID", "MemberID", "IsAbstract", "ComparisonOperator",
                "UnaryOperator", "Order", "Level", "ParentMemberID", "HierarchyNodeLabel", "ConceptID", "Path",
            ]);

        var nextHierarchyNodeId = 1;
        var hierarchiesWithoutNodes = 0;

        // Deterministic order: by HierarchyID (the order in which the dictionary loader assigned it).
        foreach (var (subCategoryId, hierarchy) in hierarchyBySubCategoryId.OrderBy(kv => kv.Value.HierarchyId))
        {
            if (!effectiveVersionsBySubCategoryId.TryGetValue(subCategoryId, out var effectiveVersion)
                || !itemsByVid.TryGetValue(effectiveVersion.SubCategoryVId, out var nodes)
                || nodes.Count == 0)
            {
                // No items in the effective version (11 of the 53 without a current version, plus
                // those that already had no items in any version in the rest of the population —
                // measured, not repaired).
                hierarchiesWithoutNodes++;
                continue;
            }

            var release = effectiveVersion.Release; // the release of THIS emitted version.
            var nodesByItemId = nodes.ToDictionary(n => n.ItemId);

            foreach (var node in nodes)
            {
                var memberId = ResolveMemberId(node.ItemId, release, hierarchy.DomainId, hierarchy.HierarchyId);
                var (path, level, parentMemberId) = BuildPathLevelAndParent(node, nodesByItemId, id => ResolveMemberId(id, release, hierarchy.DomainId, hierarchy.HierarchyId), hierarchy.HierarchyId);
                var label = itemNameById.GetValueOrDefault(node.ItemId);

                var comparisonOperator = node.ComparisonOperatorId is { } comparisonOperatorId
                    ? operatorSymbolById.GetValueOrDefault(comparisonOperatorId)
                    : null;

                // Empty string and NOT NULL when there is no unary operator.
                var unaryOperator = node.ArithmeticOperatorId is { } arithmeticOperatorId
                    ? operatorSymbolById.GetValueOrDefault(arithmeticOperatorId) ?? string.Empty
                    : string.Empty;

                writer.AddRow(
                    nextHierarchyNodeId,
                    hierarchy.HierarchyId,
                    memberId,
                    false, // IsAbstract: constant 0
                    comparisonOperator,
                    unaryOperator,
                    node.Order, // verbatim, the reference's order is not pursued
                    level, // built, not copied (the source does not carry it)
                    parentMemberId, // same
                    label,
                    null, // ConceptID: NULL here, minted by Dpm20ConceptLoader in its final phase
                    path); // built with the usual path form (dot as separator and terminator)

                nextHierarchyNodeId++;
            }
        }

        return new Result((int)writer.RowsWritten, hierarchiesWithoutNodes, anchoredMembers);
    }

    /// <summary>
    /// Builds <c>Path</c>/<c>Level</c>/<c>ParentMemberID</c> by climbing through <c>ParentItemID</c>
    /// from <paramref name="node"/> to the root of its subcategory, with the usual path form: the
    /// dot as separator and terminator, <c>MemberID</c> (not <c>ItemID</c>) in each segment.
    /// Guarded, in this order, against a null <c>ParentItemID</c> (end, root found), the
    /// self-reference of a root (end, root found) and an already visited cycle. An ALREADY
    /// VISITED cycle stops the conversion just like the missing parent below — they are the same
    /// case, and before this guard the cycle was silently cut, leaving truncated
    /// <c>Path</c>/<c>Level</c>. Measured 0 cases over the current 16,538 rows: the guard is for
    /// the next Access database and changes no output today. A <c>ParentItemID</c> that points to
    /// an <c>ItemID</c> absent from this SAME subcategory (effective version) is NOT absorbed as a
    /// root: it stops the conversion — measured 0 cases today.
    /// </summary>
    private static (string Path, int Level, int? ParentMemberId) BuildPathLevelAndParent(
        Dpm20SubCategoryItemRow node,
        IReadOnlyDictionary<int, Dpm20SubCategoryItemRow> nodesByItemId,
        Func<int, int> resolveMemberId,
        int hierarchyId)
    {
        var chain = new List<int> { node.ItemId };
        var visited = new HashSet<int> { node.ItemId };
        var current = node;

        while (true)
        {
            var parentItemId = current.ParentItemId;

            if (parentItemId is null || parentItemId.Value == current.ItemId)
            {
                break; // end: root found (no parent, or self-referenced).
            }

            if (!visited.Add(parentItemId.Value))
            {
                // Same case as the missing parent below — a cycle stops the conversion, it is not
                // silently repaired by truncating Path/Level.
                throw new InvalidOperationException(
                    $"Cycle detected while building the Path of node ItemID={node.ItemId} of "
                    + $"HierarchyID={hierarchyId}: item ItemID={current.ItemId} has ParentItemID="
                    + $"{parentItemId.Value}, which already appears in the ancestor chain of this same "
                    + "node. It is not silently repaired: it would leave Path/Level truncated.");
            }

            if (!nodesByItemId.TryGetValue(parentItemId.Value, out var parentNode))
            {
                throw new InvalidOperationException(
                    $"Cannot build the Path of node ItemID={node.ItemId} of "
                    + $"HierarchyID={hierarchyId}: item ItemID={current.ItemId} has ParentItemID="
                    + $"{parentItemId.Value}, which is not a node of this same subcategory (effective "
                    + "version). It is not silently repaired: it would graft a subtree in the wrong place.");
            }

            chain.Add(parentItemId.Value);
            current = parentNode;
        }

        chain.Reverse();
        var memberChain = chain.Select(resolveMemberId).ToList();
        var path = string.Concat(memberChain.Select(id => id.ToString(CultureInfo.InvariantCulture) + "."));
        var level = memberChain.Count;
        var parentMemberId = memberChain.Count >= 2 ? memberChain[^2] : (int?)null;

        return (path, level, parentMemberId);
    }

    /// <summary>
    /// Per subcategory, the current version (at the cutoff, <see cref="Dpm20AccessReader.CutoffReleaseId"/>)
    /// if it exists (1,101 of 1,154, measured with no ties); otherwise the one with the highest
    /// <c>StartReleaseID</c> (the remaining 53, also measured with no ties). It ALSO returns the
    /// <c>Release</c> that this version represents — the cutoff for the 1,101 current ones, the
    /// <c>StartReleaseID</c> of the version itself for the 53 without a current one — because it is
    /// the time coordinate at which the <c>ItemID</c> of its nodes must be resolved (see
    /// <see cref="Load"/>): the EMITTED version of a fallback subcategory is not the current one,
    /// so resolving its items at the cutoff would mix two different releases.
    /// </summary>
    private static Dictionary<int, (int SubCategoryVId, int Release)> ResolveEffectiveSubCategoryVersions(
        IEnumerable<Dpm20SubCategoryVersionRow> allVersions, int cutoff)
    {
        var bySubCategoryId = new Dictionary<int, List<Dpm20SubCategoryVersionRow>>();
        foreach (var version in allVersions)
        {
            if (!bySubCategoryId.TryGetValue(version.SubCategoryId, out var list))
            {
                list = [];
                bySubCategoryId[version.SubCategoryId] = list;
            }

            list.Add(version);
        }

        var result = new Dictionary<int, (int SubCategoryVId, int Release)>(bySubCategoryId.Count);
        foreach (var (subCategoryId, versions) in bySubCategoryId)
        {
            var currentVersion = versions.FirstOrDefault(v => IsCurrent(v.StartReleaseId, v.EndReleaseId, cutoff));
            result[subCategoryId] = currentVersion is not null
                ? (currentVersion.SubCategoryVId, cutoff)
                : SelectLast(versions);
        }

        return result;

        static (int SubCategoryVId, int Release) SelectLast(List<Dpm20SubCategoryVersionRow> versions)
        {
            var last = versions.OrderByDescending(v => v.StartReleaseId).First();
            return (last.SubCategoryVId, last.StartReleaseId);
        }
    }

    private static bool IsCurrent(int startReleaseId, int? endReleaseId, int cutoff)
        => startReleaseId <= cutoff && (endReleaseId is null || endReleaseId.Value > cutoff);

    /// <summary>
    /// The <c>(UnionDomainID, UnitedDomainID)</c> pairs already written by
    /// <c>Dpm20DictionaryLoader</c> — the SAME relation used by <c>I-TRE-07b</c> so as not to
    /// count as a violation a union-domain hierarchy that references its components. The anchor of
    /// <c>ResolveMemberId</c> must not "correct" what is already a deliberate relation.
    /// </summary>
    private static HashSet<(int UnionDomainId, int UnitedDomainId)> ReadDomainUnionPairs(SqliteConnection destination)
    {
        var result = new HashSet<(int, int)>();

        using var command = destination.CreateCommand();
        command.CommandText = "SELECT \"UnionDomainID\", \"UnitedDomainID\" FROM mDomainUnion";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }

        return result;
    }

    /// <summary><c>DomainID</c> by <c>DomainCode</c>, re-read from <c>mDomain</c> (already written by <see cref="Dpm20DictionaryLoader"/>).</summary>
    private static Dictionary<string, int> ReadDomainIdByDomainCode(SqliteConnection destination)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        using var command = destination.CreateCommand();
        command.CommandText = "SELECT \"DomainID\", \"DomainCode\" FROM mDomain";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var domainId = reader.GetInt32(0);
            var domainCode = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            result[domainCode] = domainId;
        }

        return result;
    }

    /// <summary>
    /// <c>MemberID</c> by (<c>DomainID</c>, <c>MemberCode</c>), re-read from <c>mMember</c>
    /// (already written by <see cref="Dpm20DictionaryLoader"/>).
    /// <paramref name="memberLabelById"/>: <c>MemberLabel</c> by <c>MemberID</c>, the business
    /// identity signal that distinguishes a genuine twin (same code, same label, two domains)
    /// from an unrelated code collision — see <c>ResolveMemberId</c>.
    /// </summary>
    private static Dictionary<(int DomainId, string Code), int> ReadMemberIdByDomainAndCode(
        SqliteConnection destination, out Dictionary<int, string> memberLabelById)
    {
        var result = new Dictionary<(int, string), int>();
        var labels = new Dictionary<int, string>();

        using var command = destination.CreateCommand();
        command.CommandText = "SELECT \"MemberID\", \"DomainID\", \"MemberCode\", \"MemberLabel\" FROM mMember";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var memberId = reader.GetInt32(0);
            var domainId = reader.GetInt32(1);
            var code = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var label = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            result[(domainId, code)] = memberId;
            labels[memberId] = label;
        }

        memberLabelById = labels;
        return result;
    }
}
