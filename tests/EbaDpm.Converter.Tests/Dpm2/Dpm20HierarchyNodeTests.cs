using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// <c>mHierarchyNode</c> -- the <c>SubCategoryItem</c> tree of the EFFECTIVE version of each
/// subcategory. It reuses <see cref="Dpm20SkeletonFixture"/> (collection <c>Dpm2Skeleton</c>): the
/// same <c>--all</c> conversion already loads <c>mHierarchyNode</c>
/// (<see cref="EbaDpm.Converter.Core.Mapping.Dpm20.Dpm20HierarchyNodeLoader"/> runs without a
/// separate flag).
///
/// <c>Order</c> is NOT compared against the reference (it has two incompatible generations; direct
/// mapping matches 0 of 2,631 on the "legacy" generation). The only verifiable property is the
/// internal invariant: within a group of siblings, the RELATIVE order of the source is preserved
/// (<see cref="Order_HasNoTiesWithinAnySiblingGroup_PreservingTheSourceRelativeOrder"/>).
///
/// <c>mOrdinateCategorisation</c> is not touched here (it is not compared by count, and it is not
/// this table). The same principles apply: censuses by CONTAINMENT, never by equality, and no
/// exception is applied as a general rule.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class Dpm20HierarchyNodeTests(Dpm20SkeletonFixture fixture)
{
    // ------------------------------------------------------------------
    // Count: 16,538 rows over the population ACTUALLY emitted (current versions + hierarchies whose
    // last version is used + the union-domain resolution), not the 13,440 "comparable" ones, which
    // were a narrower population.
    // ------------------------------------------------------------------

    [DataFact]
    public void MHierarchyNode_Has16538Rows() => Assert.Equal(16538, CountRows("mHierarchyNode"));

    /// <summary>
    /// 14 emitted hierarchies are left without nodes, NAMED one by one -- not a loose count, because
    /// an equal count does not distinguish "the right 14" from "any 14" after a regression. 11 are
    /// <c>EC</c> 1-11 (no current version and no items in their last version either) + 3 with a
    /// current version and ZERO items, genuinely empty in the source (<c>CP:CP3</c>,
    /// <c>IM:test</c>, <c>qOR:new_OR7</c>).
    /// </summary>
    [DataFact]
    public void ExactlyThe14MeasuredHierarchies_AreEmittedWithoutNodes()
    {
        var withoutNodes = ReadPairs(
            """
            SELECT d."DomainCode", h."HierarchyCode" FROM "mHierarchy" h
            JOIN "mDomain" d ON d."DomainID" = h."DomainID"
            WHERE NOT EXISTS (SELECT 1 FROM "mHierarchyNode" n WHERE n."HierarchyID" = h."HierarchyID")
            """)
            .OrderBy(p => p.A, StringComparer.Ordinal)
            .ThenBy(p => p.B, StringComparer.Ordinal)
            .ToList();

        var expected = new[]
        {
            ("CP", "CP3"),
            ("EC", "EC1"), ("EC", "EC10"), ("EC", "EC11"), ("EC", "EC2"), ("EC", "EC3"),
            ("EC", "EC4"), ("EC", "EC5"), ("EC", "EC6"), ("EC", "EC7"), ("EC", "EC8"), ("EC", "EC9"),
            ("IM", "test"),
            ("qOR", "new_OR7"),
        }
        .OrderBy(p => p.Item1, StringComparer.Ordinal)
        .ThenBy(p => p.Item2, StringComparer.Ordinal)
        .ToList();

        Assert.Equal(14, withoutNodes.Count);
        Assert.Equal(expected, withoutNodes.Select(p => (p.A, p.B)).ToList());
    }

    // ------------------------------------------------------------------
    // Referential and tree integrity.
    // ------------------------------------------------------------------

    [DataFact]
    public void NoNode_HasADanglingHierarchyID()
    {
        var dangling = ScalarLong(
            """
            SELECT COUNT(*) FROM "mHierarchyNode" n
            WHERE NOT EXISTS (SELECT 1 FROM "mHierarchy" h WHERE h."HierarchyID" = n."HierarchyID")
            """);
        Assert.True(dangling == 0, $"{dangling} mHierarchyNode rows with a HierarchyID that does not exist in mHierarchy.");
    }

    [DataFact]
    public void NoNode_HasADanglingMemberID()
    {
        var dangling = ScalarLong(
            """
            SELECT COUNT(*) FROM "mHierarchyNode" n
            WHERE NOT EXISTS (SELECT 1 FROM "mMember" m WHERE m."MemberID" = n."MemberID")
            """);
        Assert.True(dangling == 0, $"{dangling} mHierarchyNode rows with a MemberID that does not exist in mMember.");
    }

    /// <summary>
    /// No <c>ParentMemberID</c> points to a member that is not a NODE OF ITS OWN HIERARCHY -- it is
    /// not enough for the member to exist in <c>mMember</c>: it must be a <c>mHierarchyNode</c> row
    /// of THAT <c>HierarchyID</c>. It is the check, over the written .db, of the guard that the
    /// loader documents as "stops the conversion, is not repaired": if this failed, a ParentItemID
    /// outside the subcategory would have slipped through without throwing.
    /// </summary>
    [DataFact]
    public void ParentMemberID_IsAlwaysANodeOfItsOwnHierarchy()
    {
        var offenders = ScalarLong(
            """
            SELECT COUNT(*) FROM "mHierarchyNode" n
            WHERE n."ParentMemberID" IS NOT NULL
            AND NOT EXISTS (
                SELECT 1 FROM "mHierarchyNode" n2
                WHERE n2."HierarchyID" = n."HierarchyID" AND n2."MemberID" = n."ParentMemberID"
            )
            """);
        Assert.True(offenders == 0, $"{offenders} nodes with a ParentMemberID that is not a node of their own hierarchy.");
    }

    /// <summary>
    /// No cycle in the tree: from EVERY node, climbing through <c>ParentMemberID</c> (within its own
    /// hierarchy) ends at a root (<c>ParentMemberID IS NULL</c>) without revisiting an already seen
    /// node. It is checked over the WRITTEN .db, not by re-reading the loader algorithm: if the
    /// loader had a bug that let a cycle through without throwing (its "visited" guard cuts the
    /// loop during construction but does not throw), this is the check that would detect it.
    /// </summary>
    [DataFact]
    public void HierarchyTree_HasNoCycles()
    {
        var edges = ReadTriplesLongLongLong(
            "SELECT \"HierarchyID\", \"MemberID\", \"ParentMemberID\" FROM \"mHierarchyNode\"");

        var parentByHierarchyAndMember = new Dictionary<(long HierarchyId, long MemberId), long?>();
        var nodeCountByHierarchy = new Dictionary<long, int>();
        foreach (var (hierarchyId, memberId, parentMemberId) in edges)
        {
            parentByHierarchyAndMember[(hierarchyId, memberId)] = parentMemberId;
            nodeCountByHierarchy[hierarchyId] = nodeCountByHierarchy.GetValueOrDefault(hierarchyId) + 1;
        }

        var cyclic = new List<string>();
        foreach (var (hierarchyId, memberId, _) in edges)
        {
            var seen = new HashSet<long> { memberId };
            var current = memberId;
            var maxSteps = nodeCountByHierarchy[hierarchyId] + 1;
            var steps = 0;
            var isCyclic = false;

            while (parentByHierarchyAndMember.TryGetValue((hierarchyId, current), out var parent) && parent is not null)
            {
                if (!seen.Add(parent.Value) || ++steps > maxSteps)
                {
                    isCyclic = true;
                    break;
                }

                current = parent.Value;
            }

            if (isCyclic)
            {
                cyclic.Add($"HierarchyID={hierarchyId}, MemberID={memberId}");
            }
        }

        Assert.True(cyclic.Count == 0, $"{cyclic.Count} nodes whose ParentMemberID chain enters a cycle. Examples: {string.Join(" | ", cyclic.Take(10))}.");
    }

    // ------------------------------------------------------------------
    // Level = number of Path segments, and Path ends in the node's own MemberID.
    // ------------------------------------------------------------------

    [DataFact]
    public void Level_EqualsPathSegmentCount_AndPath_EndsInOwnMemberId()
    {
        var rows = ReadHierarchyNodeShape();
        Assert.True(rows.Count > 0, "mHierarchyNode is empty: nothing to check.");

        var malformed = new List<string>();
        var levelMismatch = new List<string>();
        var pathEndMismatch = new List<string>();

        foreach (var row in rows)
        {
            if (row.Path is null || row.Path.Length == 0 || row.Path[^1] != '.')
            {
                malformed.Add($"HierarchyNodeID={row.HierarchyNodeId}, Path='{row.Path}'");
                continue;
            }

            var segments = row.Path.Split('.', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length != row.Level)
            {
                levelMismatch.Add($"HierarchyNodeID={row.HierarchyNodeId}, Level={row.Level}, segments={segments.Length}, Path='{row.Path}'");
            }

            if (segments.Length == 0 || segments[^1] != row.MemberId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                pathEndMismatch.Add($"HierarchyNodeID={row.HierarchyNodeId}, MemberID={row.MemberId}, Path='{row.Path}'");
            }
        }

        Assert.True(malformed.Count == 0, $"{malformed.Count} rows with a malformed Path (does not end in '.'). Examples: {string.Join(" | ", malformed.Take(10))}.");
        Assert.True(levelMismatch.Count == 0, $"{levelMismatch.Count} rows where Level is not the number of Path segments. Examples: {string.Join(" | ", levelMismatch.Take(10))}.");
        Assert.True(pathEndMismatch.Count == 0, $"{pathEndMismatch.Count} rows where Path does not end in the node's own MemberID. Examples: {string.Join(" | ", pathEndMismatch.Take(10))}.");
    }

    // ------------------------------------------------------------------
    // UnaryOperator NEVER NULL (empty string when there is none), IsAbstract always 0.
    // ------------------------------------------------------------------

    /// <summary>
    /// NULL and the empty string are DIFFERENT things and are checked separately -- an assertion of
    /// the <c>COALESCE(UnaryOperator,'') = UnaryOperator</c> kind does not distinguish "came as
    /// NULL" from "came as ''" and would let through a regression that reintroduced NULL with both
    /// halves green by accident.
    /// </summary>
    [DataFact]
    public void UnaryOperator_IsNeverNull_UsesEmptyStringInstead()
    {
        var nullCount = ScalarLong("SELECT COUNT(*) FROM \"mHierarchyNode\" WHERE \"UnaryOperator\" IS NULL");
        Assert.True(nullCount == 0, $"{nullCount} rows with a NULL UnaryOperator (it must be an empty string, never NULL).");

        var emptyCount = ScalarLong("SELECT COUNT(*) FROM \"mHierarchyNode\" WHERE \"UnaryOperator\" = ''");
        Assert.True(emptyCount > 0, "0 rows with UnaryOperator = '': suspiciously low figure, review before accepting (if the empty string never appears, the NULL half could be slipping through disguised as something else).");

        var total = CountRows("mHierarchyNode");
        var nonEmptyNonNull = ScalarLong("SELECT COUNT(*) FROM \"mHierarchyNode\" WHERE \"UnaryOperator\" <> '' AND \"UnaryOperator\" IS NOT NULL");
        Assert.Equal(total, emptyCount + nonEmptyNonNull);
    }

    [DataFact]
    public void IsAbstract_IsAlwaysZero()
    {
        var nonZero = ScalarLong("SELECT COUNT(*) FROM \"mHierarchyNode\" WHERE \"IsAbstract\" <> 0");
        Assert.True(nonZero == 0, $"{nonZero} rows with IsAbstract different from 0 (the source has no such trait, always 0).");
    }

    // ------------------------------------------------------------------
    // No repeated (hierarchy, member) pair -- WITH the domain in the key.
    // ------------------------------------------------------------------

    /// <summary>
    /// "No repeated (hierarchy, member) pair" is ONLY true with the key that carries the DOMAIN.
    /// <c>(HierarchyID, MemberID)</c> already carries it implicitly -- <c>mMember</c> is split by
    /// <c>(DomainID, MemberCode)</c>, so two members with the same CODE in different domains have a
    /// different <c>MemberID</c> -- and with that key the count is 0 over all 16,538 rows. The
    /// POSITIVE evidence of the trap is also kept, with the same data: grouping by
    /// <c>(HierarchyID, MemberCode)</c> WITHOUT the domain exactly 8 false duplicates appear --
    /// the same <c>qx*</c> code living in two distinct components of a hierarchy over a union
    /// domain. If one day this second number stops being 8, the phenomenon changed, not the
    /// correct key.
    /// </summary>
    [DataFact]
    public void NoHierarchyMemberPairIsDuplicated_WithTheDomainQualifiedKey()
    {
        var correctKeyDuplicates = ScalarLong(
            """
            SELECT COUNT(*) FROM (
                SELECT "HierarchyID", "MemberID" FROM "mHierarchyNode"
                GROUP BY "HierarchyID", "MemberID" HAVING COUNT(*) > 1
            )
            """);
        Assert.True(correctKeyDuplicates == 0, $"{correctKeyDuplicates} duplicated (HierarchyID, MemberID) pairs in mHierarchyNode (0 expected).");

        // Positive evidence of the trap: WITHOUT the domain in the key, 8 false duplicates appear.
        // This is NOT a failure -- it is the proof that the correct key matters.
        var withoutDomainDuplicates = ScalarLong(
            """
            SELECT COUNT(*) FROM (
                SELECT n."HierarchyID", m."MemberCode" FROM "mHierarchyNode" n
                JOIN "mMember" m ON m."MemberID" = n."MemberID"
                GROUP BY n."HierarchyID", m."MemberCode" HAVING COUNT(*) > 1
            )
            """);
        Assert.True(
            withoutDomainDuplicates == 8,
            $"{withoutDomainDuplicates} 'duplicated' (HierarchyID, MemberCode WITHOUT domain) pairs (exactly 8 " +
            "were expected: the same code lives in several components of a union domain). If this " +
            "number changes, the phenomenon changed and the note has to be reviewed, not the number " +
            "adjusted on the fly.");
    }

    // ------------------------------------------------------------------
    // Order is NOT compared with the reference. Only the internal invariant: no ties within a group
    // of siblings, so the verbatim order copied from the source keeps the relative one.
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>SubCategoryItem.Order</c> is emitted VERBATIM (the reference's is not pursued, since it
    /// has two incompatible generations). The only checkable property is the internal invariant
    /// that must ALWAYS hold for "relative order preserved" to mean anything: within each group of
    /// siblings (same <c>HierarchyID</c>, same <c>ParentMemberID</c> -- including the root, with
    /// <c>ParentMemberID IS NULL</c>), <c>Order</c> has no ties. Since the value is a literal copy of
    /// the source, the absence of ties implies that sorting by <c>Order</c> reproduces EXACTLY the
    /// order of <c>SubCategoryItem</c> -- without it, "relative" would have no deterministic meaning.
    /// </summary>
    [DataFact]
    public void Order_HasNoTiesWithinAnySiblingGroup_PreservingTheSourceRelativeOrder()
    {
        var siblingGroupsWithTies = ScalarLong(
            """
            SELECT COUNT(*) FROM (
                SELECT "HierarchyID", "ParentMemberID", COUNT(*) AS n, COUNT(DISTINCT "Order") AS distinctOrders
                FROM "mHierarchyNode"
                GROUP BY "HierarchyID", "ParentMemberID"
                HAVING n <> distinctOrders
            )
            """);

        var totalSiblingGroups = ScalarLong(
            """
            SELECT COUNT(*) FROM (
                SELECT "HierarchyID", "ParentMemberID" FROM "mHierarchyNode" GROUP BY "HierarchyID", "ParentMemberID"
            )
            """);
        Assert.True(totalSiblingGroups > 0, "0 sibling groups: the comparison would prove nothing.");

        Assert.True(
            siblingGroupsWithTies == 0,
            $"{siblingGroupsWithTies} of {totalSiblingGroups} sibling groups with a tied Order " +
            "(the verbatim source Order must distinguish siblings from each other for the " +
            "relative order to make sense).");
    }

    // ------------------------------------------------------------------
    // Nominal case - new_IG1 over the UNION domain qIG, with items resolved from GA.
    // ------------------------------------------------------------------

    /// <summary>
    /// Nominal case. <c>new_IG1</c> hangs (through <c>mHierarchy.DomainID</c>) from the UNION domain
    /// <c>qIG</c>, but its items live in <c>ItemCategory</c> under the category of the COMPONENT
    /// domain <c>GA</c> -- the GLOBAL resolution of <c>ItemID</c> must find them just the same.
    /// Measured independently: 359 nodes, 100% with <c>mMember.DomainID</c> resolved to <c>GA</c>
    /// (none stays in <c>qIG</c>, which has no members of its own).
    /// </summary>
    [DataFact]
    public void NewIG1OnTheUnionDomainQIG_ResolvesAllItsNodesFromTheComponentDomainGA()
    {
        var nodeCount = ScalarLong(
            """
            SELECT COUNT(*) FROM "mHierarchyNode" n
            JOIN "mHierarchy" h ON h."HierarchyID" = n."HierarchyID"
            JOIN "mDomain" d ON d."DomainID" = h."DomainID"
            WHERE d."DomainCode" = 'qIG' AND h."HierarchyCode" = 'new_IG1'
            """);
        Assert.True(nodeCount > 0, "new_IG1 over qIG has no nodes: the nominal case cannot be checked (was it lost along the way?).");

        var resolvedFromGa = ScalarLong(
            """
            SELECT COUNT(*) FROM "mHierarchyNode" n
            JOIN "mHierarchy" h ON h."HierarchyID" = n."HierarchyID"
            JOIN "mDomain" d ON d."DomainID" = h."DomainID"
            JOIN "mMember" m ON m."MemberID" = n."MemberID"
            JOIN "mDomain" d2 ON d2."DomainID" = m."DomainID"
            WHERE d."DomainCode" = 'qIG' AND h."HierarchyCode" = 'new_IG1' AND d2."DomainCode" = 'GA'
            """);

        Assert.True(
            resolvedFromGa == nodeCount,
            $"new_IG1 has {nodeCount} nodes but only {resolvedFromGa} resolve their member to the " +
            "component domain GA: all of them should, because qIG (the union domain) has no members of its own.");
    }

    // ------------------------------------------------------------------
    // Comparison by business key against the reference: FIXED CENSUS, not a percentage.
    // ------------------------------------------------------------------

    /// <summary>
    /// Over what is ACTUALLY emitted (16,538 nodes, not the 13,440 "comparable" ones), the business
    /// key is (domain, hierarchy code) to match the hierarchy, and the set of <c>MemberCode</c>
    /// values of its nodes to compare the content. Measured: 993 common keys, 930 exact, 55
    /// contained (one of the two is a subset of the other, without being exactly equal) and 8 with
    /// no containment in either direction. The CENSUS of the 8 that do not match is fixed, not just
    /// their count -- so that a regression that changed WHICH ones they are (instead of how many)
    /// does not go unnoticed.
    /// </summary>
    [DataFact]
    public void HierarchyMemberSets_ByBusinessKey_MatchTheMeasuredCensus_993Common_932Exact_55Contained_6NoContainment()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var generatedMap = ReadHierarchyMemberSets(fixture.GeneratedConnection);

        using var referenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);
        var referenceMap = ReadHierarchyMemberSets(referenceConnection);

        var commonKeys = generatedMap.Keys.Intersect(referenceMap.Keys).OrderBy(k => k.Domain, StringComparer.Ordinal).ThenBy(k => k.Hierarchy, StringComparer.Ordinal).ToList();
        Assert.Equal(993, commonKeys.Count);

        var exact = new List<(string Domain, string Hierarchy)>();
        var contained = new List<(string Domain, string Hierarchy)>();
        var noContainment = new List<(string Domain, string Hierarchy)>();

        foreach (var key in commonKeys)
        {
            var generated = generatedMap[key];
            var reference = referenceMap[key];

            if (generated.SetEquals(reference))
            {
                exact.Add(key);
            }
            else if (reference.IsSubsetOf(generated) || generated.IsSubsetOf(reference))
            {
                contained.Add(key);
            }
            else
            {
                noContainment.Add(key);
            }
        }

        // 930 -> 932 on "DPM2 Database_v 4_2_1.accdb" (cutoff 4.2): the EBA edited in place the items of
        // two in-force subcategory versions, and both hierarchies now equal the reference exactly.
        // qTR_5 (SubCategoryVID 20598, Start 5, same version in both editions), ItemCategory codes of
        // its SubCategoryItem rows, measured on the Access sources:
        //   previous edition: qx2001, qx2035, qx2040, qx2042, qx2052, qx2053, qx2054
        //   4.2.1 edition:    qx2035, qx2040, qx2042, qx2065, qx2066, qx2067, qx2068
        // new_IG1 (SubCategoryVID 20534): member GA:XK became GA:qx2000 in the generated output.
        Assert.Equal(932, exact.Count);
        Assert.Equal(55, contained.Count);

        // The fixed CENSUS of the 8 with no containment in either direction: the ItemID is resolved
        // in the release of the subcategory version that is emitted, not in a fixed cutoff. Those 8
        // are the pure remainder where the ItemID has more than one code: we emit the current one,
        // the reference kept an old one.
        var expectedNoContainment = new[]
        {
            ("GA", "GA6"),
            ("qAO", "AO_agg_3"),
            ("qCG", "new_CG0"), ("qCG", "new_CG10"), ("qCG", "new_CG7"), ("qCG", "new_CG8"),
        }
        .OrderBy(p => p.Item1, StringComparer.Ordinal)
        .ThenBy(p => p.Item2, StringComparer.Ordinal)
        .ToList();

        var actualNoContainment = noContainment.OrderBy(k => k.Domain, StringComparer.Ordinal).ThenBy(k => k.Hierarchy, StringComparer.Ordinal).ToList();

        Assert.Equal(6, actualNoContainment.Count);
        Assert.Equal(expectedNoContainment, actualNoContainment.Select(k => (k.Domain, k.Hierarchy)).ToList());
    }

    // ------------------------------------------------------------------
    // DECISION, not a defect -- HierarchyNode descriptions are deliberately NOT emitted.
    // ------------------------------------------------------------------

    /// <summary>
    /// DELIBERATE DECISION, not a gap to fix. The candidate rule was "a node carries a description
    /// iff its member does, with the same text", but the 12 texts that the reference exposes (legal
    /// citations: <c>CRR 50</c>, <c>CRR 107 ¶1.a</c>, <c>CRR 4 ¶86</c>...) DO NOT EXIST anywhere in
    /// the DPM 2.0 Access database -- <c>Item.Description</c> covers 1 of 12 and with a different
    /// text; <c>Reference</c>, <c>Translation</c>, <c>Role</c> and <c>DPMAttribute</c>: 0 rows.
    /// Emitting them would require seeding those 12 citations as a literal constant copied from the
    /// reference <c>.db</c>, and the reference database is not a specification (the Access database
    /// and the EBA layouts are the sources of truth). What the Access database declares is exported;
    /// here it declares nothing, which is the opposite situation. And they are descriptions: they
    /// take no part in resolving a fact to its cell, so their absence breaks no real consumption.
    ///
    /// If someone "fixes" this months from now by seeding the citations by hand, they should reread
    /// this reasoning first -- that is why this test EXISTS and why the comment is so long.
    ///
    /// Positive control: there are 16,538 <c>mHierarchyNode</c> rows (a real, non-empty candidate
    /// population) and, of them, one <c>HierarchyNode</c> concept per row -- if the check below gave
    /// 0 because there is NOTHING to look at, this positive control would catch it.
    /// </summary>
    [DataFact]
    public void MConceptTranslation_Role_Description_IsZero_ForHierarchyNode()
    {
        var hierarchyNodeConceptCount = ScalarLong("SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = 'HierarchyNode'");
        Assert.True(hierarchyNodeConceptCount > 0, "0 HierarchyNode concepts: the check below would be empty for lack of population, not because of the decision.");
        Assert.Equal(16538, hierarchyNodeConceptCount); // unchanged: neither mHierarchyNode nor mConcept is touched by the description decision.

        var descriptionRowsForHierarchyNode = ScalarLong(
            """
            SELECT COUNT(*) FROM "mConceptTranslation" ct
            JOIN "mConcept" c ON c."ConceptID" = ct."ConceptID"
            WHERE c."ConceptType" = 'HierarchyNode' AND ct."Role" = 'description'
            """);
        Assert.Equal(0, descriptionRowsForHierarchyNode);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Dictionary<(string Domain, string Hierarchy), HashSet<string>> ReadHierarchyMemberSets(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT d."DomainCode", h."HierarchyCode", m."MemberCode"
            FROM "mHierarchyNode" n
            JOIN "mHierarchy" h ON h."HierarchyID" = n."HierarchyID"
            JOIN "mDomain" d ON d."DomainID" = h."DomainID"
            JOIN "mMember" m ON m."MemberID" = n."MemberID"
            """;

        var result = new Dictionary<(string, string), HashSet<string>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var domain = reader.GetString(0);
            var hierarchy = reader.GetString(1);
            var member = reader.GetString(2);

            var key = (domain, hierarchy);
            if (!result.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                result[key] = set;
            }

            set.Add(member);
        }

        return result;
    }

    private sealed record HierarchyNodeShape(long HierarchyNodeId, long MemberId, long Level, string? Path);

    private List<HierarchyNodeShape> ReadHierarchyNodeShape()
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = "SELECT \"HierarchyNodeID\", \"MemberID\", \"Level\", \"Path\" FROM \"mHierarchyNode\"";
        using var reader = command.ExecuteReader();

        var rows = new List<HierarchyNodeShape>();
        while (reader.Read())
        {
            rows.Add(new HierarchyNodeShape(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    private List<(long A, long B, long? C)> ReadTriplesLongLongLong(string sql)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var rows = new List<(long, long, long?)>();
        while (reader.Read())
        {
            rows.Add((
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }

        return rows;
    }

    private List<(string A, string B)> ReadPairs(string sql)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var rows = new List<(string, string)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    private long CountRows(string table)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private long ScalarLong(string sql)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
