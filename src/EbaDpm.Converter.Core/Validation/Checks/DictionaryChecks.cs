using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.Checks;

/// <summary>
/// Dictionary, sentinels and hierarchies. Plane A, without looking at any reference.
/// </summary>
public static class DictionaryChecks
{
    public static IEnumerable<CheckResult> Run(SqliteConnection c, ValidationSourceModel model, List<KnownExceptions.Outcome> exceptionSink)
    {
        // ---- A-DIC-01/02/03: typed domain <=> no default member ----
        yield return SqlHelpers.CountCheck(
            c, "A-DIC-01", ValidationLayer.Critical, "mDomain", "TYPED domain => no default member",
            "SELECT COUNT(*) FROM \"mDomain\" WHERE \"IsTypedDomain\" = 1",
            """
            SELECT COUNT(*) FROM "mDomain" d
            WHERE d."IsTypedDomain" = 1
              AND EXISTS (SELECT 1 FROM "mMember" m WHERE m."DomainID" = d."DomainID" AND m."IsDefaultMember" = 1)
            """);

        yield return NonTypedDomainsWithoutDefaultMember(c);

        yield return SqlHelpers.CountCheck(
            c, "A-DIC-03", ValidationLayer.Critical, "mDomain", "No domain with more than one default member",
            "SELECT COUNT(*) FROM \"mDomain\"",
            """
            SELECT COUNT(*) FROM (
                SELECT "DomainID" FROM "mMember" WHERE "IsDefaultMember" = 1
                GROUP BY "DomainID" HAVING COUNT(*) > 1)
            """);

        // ---- A-DIC-04: IsTypedDimension == IsTypedDomain of its domain ----
        yield return SqlHelpers.CountCheck(
            c, "A-DIC-04", ValidationLayer.Critical, "mDimension", "IsTypedDimension matches IsTypedDomain of its domain",
            "SELECT COUNT(*) FROM \"mDimension\"",
            """
            SELECT COUNT(*) FROM "mDimension" dim
            JOIN "mDomain" dom ON dom."DomainID" = dim."DomainID"
            WHERE dim."IsTypedDimension" IS NOT dom."IsTypedDomain"
            """);

        // ---- A-DIC-05: DefaultMemberID exists and belongs to the domain of its own dimension ----
        yield return SqlHelpers.CountCheck(
            c, "A-DIC-05", ValidationLayer.Critical, "mDimension", "DefaultMemberID exists and belongs to the domain of its dimension",
            "SELECT COUNT(*) FROM \"mDimension\" WHERE \"DefaultMemberID\" IS NOT NULL",
            """
            SELECT COUNT(*) FROM "mDimension" dim
            WHERE dim."DefaultMemberID" IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM "mMember" m
                  WHERE m."MemberID" = dim."DefaultMemberID" AND m."DomainID" = dim."DomainID")
            """);

        // ---- A-DIC-06: no emitted dimension with DimensionXBRLCode NULL ----
        yield return SqlHelpers.CountCheck(
            c, "A-DIC-06", ValidationLayer.Critical, "mDimension", "No dimension with DimensionXBRLCode NULL",
            "SELECT COUNT(*) FROM \"mDimension\"",
            "SELECT COUNT(*) FROM \"mDimension\" WHERE \"DimensionXBRLCode\" IS NULL");

        // ---- A-DIC-07: exactly 1 MET dimension ----
        yield return ExactMatch(
            c, "A-DIC-07", "mDimension", "Exactly 1 dimension with DimensionXBRLCode='MET' and DimensionLabel='Metric dimension'",
            "SELECT COUNT(*) FROM \"mDimension\" WHERE \"DimensionXBRLCode\" = 'MET' AND \"DimensionLabel\" = 'Metric dimension'", 1);

        // ---- A-DIC-08: every metric has a corresponding member in the MET domain ----
        yield return SqlHelpers.CountCheck(
            c, "A-DIC-08", ValidationLayer.Critical, "mMetric", "Every metric has a CorrespondingMemberID in the MET domain",
            "SELECT COUNT(*) FROM \"mMetric\"",
            """
            SELECT COUNT(*) FROM "mMetric" met
            WHERE NOT EXISTS (
                SELECT 1 FROM "mMember" m JOIN "mDomain" dom ON dom."DomainID" = m."DomainID"
                WHERE m."MemberID" = met."CorrespondingMemberID" AND dom."DomainCode" = 'MET')
            """);

        // ---- A-DIC-09: |mMetric| == number of members of the MET domain ----
        yield return CountsEqual(
            c, "A-DIC-09", "mMetric/mMember(MET)", "|mMetric| == number of members of the MET domain",
            "SELECT COUNT(*) FROM \"mMetric\"",
            "SELECT COUNT(*) FROM \"mMember\" m JOIN \"mDomain\" dom ON dom.\"DomainID\" = m.\"DomainID\" WHERE dom.\"DomainCode\" = 'MET'");

        // ---- A-DIC-10: the only dimension XBRL code without ':' is MET ----
        yield return SqlHelpers.CountCheck(
            c, "A-DIC-10", ValidationLayer.Critical, "mDimension", "The only DimensionXBRLCode without ':' is 'MET'",
            "SELECT COUNT(*) FROM \"mDimension\"",
            "SELECT COUNT(*) FROM \"mDimension\" WHERE \"DimensionXBRLCode\" NOT LIKE '%:%' AND \"DimensionXBRLCode\" <> 'MET'");

        // ---- A-DIC-11: the release suffix in the XBRLCode ----
        yield return ReleaseSuffixedDimensions(c, model);

        // ---- A-DIC-12: non-null ReleaseID ONLY on Taxonomy/Dimension/Member ----
        yield return SqlHelpers.CountCheck(
            c, "A-DIC-12", ValidationLayer.Critical, "mConcept", "Non-null ReleaseID only on ConceptType Taxonomy/Dimension/Member",
            "SELECT COUNT(*) FROM \"mConcept\"",
            "SELECT COUNT(*) FROM \"mConcept\" WHERE \"ReleaseID\" IS NOT NULL AND \"ConceptType\" NOT IN ('Taxonomy','Dimension','Member')");

        // ---- A-DIC-13: mConceptTranslation Role/LanguageID ----
        yield return SqlHelpers.CountCheck(
            c, "A-DIC-13.1", ValidationLayer.Critical, "mConceptTranslation", "Role belongs to {label, description}",
            "SELECT COUNT(*) FROM \"mConceptTranslation\"",
            "SELECT COUNT(*) FROM \"mConceptTranslation\" WHERE \"Role\" NOT IN ('label','description')");
        yield return SqlHelpers.CountCheck(
            c, "A-DIC-13.2", ValidationLayer.Critical, "mConceptTranslation", "LanguageID = 1 in 100% of rows",
            "SELECT COUNT(*) FROM \"mConceptTranslation\"",
            "SELECT COUNT(*) FROM \"mConceptTranslation\" WHERE \"LanguageID\" IS NOT 1");

        // ---- A-DIC-14: every concept has a label translation (informative) ----
        yield return SqlHelpers.CountCheck(
            c, "A-DIC-14", ValidationLayer.Informative, "mConcept", "Every concept has a 'label' translation",
            "SELECT COUNT(*) FROM \"mConcept\"",
            """
            SELECT COUNT(*) FROM "mConcept" co
            WHERE NOT EXISTS (SELECT 1 FROM "mConceptTranslation" ct WHERE ct."ConceptID" = co."ConceptID" AND ct."Role" = 'label')
            """);

        // ---- A-DIC-15: mOwner 3 fixed rows; mOwnerParent 1 (1->3); mLanguage 1 ('en') ----
        yield return ExactMatch(c, "A-DIC-15.1", "mOwner", "mOwner has exactly 3 rows", "SELECT COUNT(*) FROM \"mOwner\"", 3);
        yield return ExactMatch(c, "A-DIC-15.2", "mOwnerParent", "mOwnerParent has exactly 1 row", "SELECT COUNT(*) FROM \"mOwnerParent\"", 1);
        yield return ExactMatch(c, "A-DIC-15.3", "mLanguage", "mLanguage has exactly 1 row ('en')", "SELECT COUNT(*) FROM \"mLanguage\" WHERE \"IsoCode\" = 'en'", 1);

        // ---- A-DIC-16: ConceptID NULL only on the named objects ----
        yield return ConceptIdNullOnlyOnNamedObjects(c);
        yield return NullConceptIdCensus(c, model);

        // ---- A-SEN-01..07 ----
        yield return ExactMatch(
            c, "A-SEN-01", "mDomain",
            "Exactly 1 mDomain with DomainID=9999, DomainLabel='Open', IsTypedDomain=0, DataType/ConceptID NULL",
            "SELECT COUNT(*) FROM \"mDomain\" WHERE \"DomainID\" = 9999 AND \"DomainLabel\" = 'Open' AND \"IsTypedDomain\" = 0 AND \"DataType\" IS NULL AND \"ConceptID\" IS NULL",
            1);
        yield return ExactMatch(
            c, "A-SEN-02", "mMember",
            "Exactly 1 mMember with MemberID=9999, DomainID=9999, MemberLabel='Open', MemberXBRLCode NULL, IsDefaultMember=0",
            "SELECT COUNT(*) FROM \"mMember\" WHERE \"MemberID\" = 9999 AND \"DomainID\" = 9999 AND \"MemberLabel\" = 'Open' AND \"MemberXBRLCode\" IS NULL AND \"IsDefaultMember\" = 0",
            1);
        yield return SqlHelpers.CountCheck(
            c, "A-SEN-03", ValidationLayer.Critical, "mHierarchyNode", "No Path of mHierarchyNode keeps the untranslated 9999 segment",
            "SELECT COUNT(*) FROM \"mHierarchyNode\"",
            "SELECT COUNT(*) FROM \"mHierarchyNode\" WHERE \"Path\" = '9999.' OR \"Path\" LIKE '9999.%' OR \"Path\" LIKE '%.9999.' OR \"Path\" LIKE '%.9999.%'");
        yield return SqlHelpers.CountCheck(
            c, "A-SEN-04", ValidationLayer.Critical, "mOrdinateCategorisation", "No row uses the SOURCE sentinel MemberID=999",
            "SELECT COUNT(*) FROM \"mOrdinateCategorisation\"",
            "SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"MemberID\" = 999");
        yield return SqlHelpers.CountCheck(
            c, "A-SEN-05", ValidationLayer.Critical, "mAxisOrdinate", "No OrdinateCode equals '999' or '999 '",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\"",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE TRIM(\"OrdinateCode\") = '999'");
        yield return SqlHelpers.CountCheck(
            c, "A-SEN-06", ValidationLayer.Critical, "mMember", "No mMember with MemberLabel='<Key value>' or DomainID NULL",
            "SELECT COUNT(*) FROM \"mMember\"",
            "SELECT COUNT(*) FROM \"mMember\" WHERE \"MemberLabel\" = '<Key value>' OR \"DomainID\" IS NULL");
        yield return CountsEqual(
            c, "A-SEN-07", "mOrdinateCategorisation/mAxis", "COUNT(categorisation MemberID=9999) == COUNT(open axis)",
            "SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"MemberID\" = 9999",
            "SELECT COUNT(*) FROM \"mAxis\" WHERE \"IsOpenAxis\" = 1");

        // ---- A-HIE-01..07: hierarchies ----
        foreach (var result in HierarchyChecks(c, model, exceptionSink))
        {
            yield return result;
        }
    }

    private static CheckResult NonTypedDomainsWithoutDefaultMember(SqliteConnection c)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = SqlHelpers.Rows(
            c,
            """
            SELECT d."DomainCode" FROM "mDomain" d
            WHERE d."IsTypedDomain" = 0 AND d."DomainID" <> 9999
              AND NOT EXISTS (SELECT 1 FROM "mMember" m WHERE m."DomainID" = d."DomainID" AND m."IsDefaultMember" = 1)
            """,
            1);
        sw.Stop();

        var expected = new HashSet<string?> { "MET", "AS" };
        var unexpected = rows.Where(r => !expected.Contains(r[0])).Select(r => new CheckSample(r[0] ?? "(NULL)")).ToList();
        var total = rows.Count + 1; // +1 for the 9999 sentinel, deliberately excluded from the query

        return CheckResult.FromViolationCount(
            "A-DIC-02", ValidationPlane.A, ValidationLayer.Informative, "mDomain",
            "NON-typed domains without a default member: only MET, AS and the 9999 sentinel",
            total, unexpected.Count, sw.ElapsedMilliseconds, unexpected);
    }

    private static CheckResult ReleaseSuffixedDimensions(SqliteConnection c, ValidationSourceModel model)
    {
        if (model == ValidationSourceModel.Dpm2)
        {
            return ReleaseSuffixIsBirthRelease(c);
        }

        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "eba_dim_4.0:EXC", "eba_dim_4.0:ECG", "eba_dim_4.0:ECB", "eba_dim_4.0:ECC", "eba_dim_4.0:ECW",
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = SqlHelpers.Rows(c, "SELECT \"DimensionXBRLCode\" FROM \"mDimension\" WHERE \"DimensionXBRLCode\" IS NOT NULL", 1);
        sw.Stop();

        var suffixed = rows
            .Select(r => r[0]!)
            .Where(code => System.Text.RegularExpressions.Regex.IsMatch(code, @"_\d+(\.\d+)*:"))
            .ToList();

        var unexpected = suffixed.Where(code => !expected.Contains(code)).Select(code => new CheckSample(code)).ToList();
        var missingExpected = expected.Except(suffixed).Select(code => new CheckSample($"(absent) {code}")).ToList();
        var samples = unexpected.Concat(missingExpected).ToList();

        return CheckResult.FromViolationCount(
            "A-DIC-11", ValidationPlane.A, ValidationLayer.Critical, "mDimension",
            "The only XBRL codes with a release suffix are the 5 that the Access source already carries that way",
            rows.Count, samples.Count, sw.ElapsedMilliseconds, samples);
    }

    /// <summary>
    /// In DPM 2.0 the whitelist of 5 codes is a DPM 1.0 property and does not apply. The statement
    /// that survives is "the suffix is the BIRTH release of the property" (its
    /// <c>StartReleaseID</c>), which can be checked without any external reference:
    /// <c>mConcept.ReleaseID</c> is already derived exactly from that suffix (26,390 rows without
    /// discrepancy). Here the same relation is checked from <c>mDimension</c>: every dimension whose
    /// <c>DimensionXBRLCode</c> carries a release suffix has, via its <c>ConceptID</c>, an
    /// <c>mConcept.ReleaseID</c> whose <c>mRelease.ReleaseCode</c> is EXACTLY that suffix.
    /// </summary>
    private static CheckResult ReleaseSuffixIsBirthRelease(SqliteConnection c)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = SqlHelpers.Rows(
            c,
            """
            SELECT dim."DimensionXBRLCode", r."ReleaseCode"
            FROM "mDimension" dim
            LEFT JOIN "mConcept" co ON co."ConceptID" = dim."ConceptID"
            LEFT JOIN "mRelease" r ON r."ReleaseID" = co."ReleaseID"
            WHERE dim."DimensionXBRLCode" IS NOT NULL
            """,
            2);
        sw.Stop();

        var suffixedCount = 0;
        var mismatches = new List<CheckSample>();
        foreach (var row in rows)
        {
            var code = row[0]!;
            var match = System.Text.RegularExpressions.Regex.Match(code, @"_(?<release>\d+(?:\.\d+)*):");
            if (!match.Success)
            {
                continue;
            }

            suffixedCount++;
            var suffixRelease = match.Groups["release"].Value;
            var releaseCode = row[1];
            if (!string.Equals(releaseCode, suffixRelease, StringComparison.Ordinal))
            {
                mismatches.Add(new CheckSample(code, releaseCode ?? "(no ReleaseID)", suffixRelease));
            }
        }

        return CheckResult.FromViolationCount(
            "A-DIC-11", ValidationPlane.A, ValidationLayer.Critical, "mDimension",
            "The release suffix of DimensionXBRLCode is the BIRTH release of the property (mConcept.ReleaseID -> mRelease.ReleaseCode)",
            suffixedCount, mismatches.Count, sw.ElapsedMilliseconds, mismatches);
    }

    private static CheckResult ConceptIdNullOnlyOnNamedObjects(SqliteConnection c)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var domains = SqlHelpers.Rows(c, "SELECT \"DomainCode\" FROM \"mDomain\" WHERE \"ConceptID\" IS NULL AND \"DomainID\" <> 9999", 1).Select(r => r[0] ?? "").ToList();
        var members = SqlHelpers.Rows(c, "SELECT \"MemberXBRLCode\" FROM \"mMember\" WHERE \"ConceptID\" IS NULL AND \"MemberID\" <> 9999", 1).Select(r => r[0] ?? "").ToList();
        var hierarchies = SqlHelpers.Rows(c, "SELECT \"HierarchyCode\" FROM \"mHierarchy\" WHERE \"ConceptID\" IS NULL", 1).Select(r => r[0] ?? "").ToList();
        var dimensions = SqlHelpers.Scalar(c, "SELECT COUNT(*) FROM \"mDimension\" WHERE \"ConceptID\" IS NULL");
        sw.Stop();

        var expectedDomains = new HashSet<string>(StringComparer.Ordinal) { "INT_NA", "STR_NA" };
        var expectedMembers = new HashSet<string>(StringComparer.Ordinal) { "eba_qEC:qx01", "eba_qAE:qx01", "eba_GA:qx2007" };
        var expectedHierarchies = new HashSet<string>(StringComparer.Ordinal) { "AP29_QAP31", "CU4_CU4_REL_4" };

        var samples = domains.Where(d => !expectedDomains.Contains(d)).Select(d => new CheckSample($"mDomain:{d}"))
            .Concat(members.Where(m => !expectedMembers.Contains(m)).Select(m => new CheckSample($"mMember:{m}")))
            .Concat(hierarchies.Where(h => !expectedHierarchies.Contains(h)).Select(h => new CheckSample($"mHierarchy:{h}")))
            .ToList();

        var total = domains.Count + members.Count + hierarchies.Count + dimensions;
        return CheckResult.FromViolationCount(
            "A-DIC-16", ValidationPlane.A, ValidationLayer.Informative, "(dictionary)",
            "ConceptID NULL only on the named objects of the known origin anomaly",
            total, samples.Count, sw.ElapsedMilliseconds, samples);
    }

    /// <summary>
    /// Exact derivable census of objects with a NULL <c>ConceptID</c>. In DPM 1.0: 2 domains,
    /// 3 members, 2 hierarchies and 0 dimensions, an anomaly of the DPM 1.0 Access. In DPM 2.0 the
    /// expected census is 0 in all four counters (a concept is minted for every emitted entity;
    /// measured 0/0/0/0 in the three references).
    /// </summary>
    private static CheckResult NullConceptIdCensus(SqliteConnection c, ValidationSourceModel model)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var domainCount = SqlHelpers.Scalar(c, "SELECT COUNT(*) FROM \"mDomain\" WHERE \"ConceptID\" IS NULL AND \"DomainID\" <> 9999");
        var memberCount = SqlHelpers.Scalar(c, "SELECT COUNT(*) FROM \"mMember\" WHERE \"ConceptID\" IS NULL AND \"MemberID\" <> 9999");
        var hierarchyCount = SqlHelpers.Scalar(c, "SELECT COUNT(*) FROM \"mHierarchy\" WHERE \"ConceptID\" IS NULL");
        var dimensionCount = SqlHelpers.Scalar(c, "SELECT COUNT(*) FROM \"mDimension\" WHERE \"ConceptID\" IS NULL");
        sw.Stop();

        var (expectedDomains, expectedMembers, expectedHierarchies, expectedDimensions, statement) = model == ValidationSourceModel.Dpm2
            ? (0, 0, 0, 0, "Exact census of objects with ConceptID NULL in DPM 2.0 (0 domains, 0 members, 0 hierarchies, 0 dimensions)")
            : (2, 3, 2, 0, "Exact census of objects with ConceptID NULL (2 domains, 3 members, 2 hierarchies, 0 dimensions)");

        var failed = (domainCount == expectedDomains ? 0 : 1) + (memberCount == expectedMembers ? 0 : 1)
            + (hierarchyCount == expectedHierarchies ? 0 : 1) + (dimensionCount == expectedDimensions ? 0 : 1);
        var samples = failed == 0
            ? []
            : new List<CheckSample> { new($"domains={domainCount}({expectedDomains}) members={memberCount}({expectedMembers}) hierarchies={hierarchyCount}({expectedHierarchies}) dimensions={dimensionCount}({expectedDimensions})") };

        return CheckResult.FromViolationCount(
            "A-DIC-16-AO2", ValidationPlane.A, ValidationLayer.Critical, "(dictionary)",
            statement,
            examined: 4, failed, sw.ElapsedMilliseconds, samples);
    }

    // ------------------------------------------------------------------
    // A-HIE-01..07
    // ------------------------------------------------------------------

    private sealed record HierarchyNode(int HierarchyId, int MemberId, int? ParentMemberId, int Level, string? Path);

    private static List<HierarchyNode> LoadHierarchyNodes(SqliteConnection c) =>
        SqlHelpers.Rows(c, "SELECT \"HierarchyID\", \"MemberID\", \"ParentMemberID\", \"Level\", \"Path\" FROM \"mHierarchyNode\"", 5)
            .Select(r => new HierarchyNode(int.Parse(r[0]!), int.Parse(r[1]!), r[2] is null ? null : int.Parse(r[2]!), int.Parse(r[3]!), r[4]))
            .ToList();

    private static IEnumerable<CheckResult> HierarchyChecks(SqliteConnection c, ValidationSourceModel model, List<KnownExceptions.Outcome> exceptionSink)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var nodes = LoadHierarchyNodes(c);
        var byKey = nodes.ToDictionary(n => (n.HierarchyId, n.MemberId));
        sw.Stop();
        var loadMs = sw.ElapsedMilliseconds;

        // A-HIE-01: no cycles
        sw.Restart();
        var cycles = new List<CheckSample>();
        foreach (var node in nodes)
        {
            var visited = new HashSet<(int, int)>();
            var current = node;
            while (current.ParentMemberId is { } parentId)
            {
                var key = (current.HierarchyId, current.MemberId);
                if (!visited.Add(key))
                {
                    cycles.Add(new CheckSample($"HierarchyID={node.HierarchyId},MemberID={node.MemberId}"));
                    break;
                }

                if (!byKey.TryGetValue((current.HierarchyId, parentId), out var parent))
                {
                    break;
                }

                current = parent;
            }
        }
        sw.Stop();
        yield return CheckResult.FromViolationCount("A-HIE-01", ValidationPlane.A, ValidationLayer.Critical, "mHierarchyNode", "No cycles in the hierarchy tree", nodes.Count, cycles.Count, loadMs + sw.ElapsedMilliseconds, cycles);

        // A-HIE-02: Level == Level(parent)+1; roots Level=1 with ParentMemberID NULL; non-roots with an existing parent
        sw.Restart();
        var levelMismatches = new List<CheckSample>();
        foreach (var node in nodes)
        {
            if (node.ParentMemberId is not { } parentId)
            {
                if (node.Level != 1)
                {
                    levelMismatches.Add(new CheckSample($"HierarchyID={node.HierarchyId},MemberID={node.MemberId} root with Level={node.Level}"));
                }

                continue;
            }

            if (!byKey.TryGetValue((node.HierarchyId, parentId), out var parent))
            {
                continue; // orphan, covered by A-INT-02
            }

            if (node.Level != parent.Level + 1)
            {
                levelMismatches.Add(new CheckSample($"HierarchyID={node.HierarchyId},MemberID={node.MemberId} Level={node.Level} parent.Level={parent.Level}"));
            }
        }
        sw.Stop();
        yield return CheckResult.FromViolationCount("A-HIE-02", ValidationPlane.A, ValidationLayer.Critical, "mHierarchyNode", "Level == Level(parent)+1; roots Level=1", nodes.Count, levelMismatches.Count, sw.ElapsedMilliseconds, levelMismatches);

        // A-HIE-03: the parent is in the same hierarchy (by construction of byKey, ParentMemberID is only looked up within the same HierarchyID; we check that no other row of the same MemberID exists in ANOTHER hierarchy that would leave the interpretation ambiguous)
        sw.Restart();
        var crossHierarchy = SqlHelpers.Scalar(
            c,
            """
            SELECT COUNT(*) FROM "mHierarchyNode" n
            JOIN "mHierarchyNode" p ON p."MemberID" = n."ParentMemberID"
            WHERE n."ParentMemberID" IS NOT NULL AND p."HierarchyID" <> n."HierarchyID"
              AND NOT EXISTS (SELECT 1 FROM "mHierarchyNode" p2 WHERE p2."HierarchyID" = n."HierarchyID" AND p2."MemberID" = n."ParentMemberID")
            """);
        sw.Stop();
        yield return CheckResult.FromViolationCount("A-HIE-03", ValidationPlane.A, ValidationLayer.Critical, "mHierarchyNode", "The parent of every node belongs to the same hierarchy", nodes.Count, crossHierarchy, sw.ElapsedMilliseconds, []);

        // A-HIE-04: Path ends with the node's own MemberID, starts with the parent's Path + '.', Level = number of segments
        sw.Restart();
        var pathMismatches = new List<CheckSample>();
        foreach (var node in nodes)
        {
            if (string.IsNullOrEmpty(node.Path))
            {
                pathMismatches.Add(new CheckSample($"HierarchyID={node.HierarchyId},MemberID={node.MemberId} Path is null"));
                continue;
            }

            var ownSegment = node.MemberId + ".";
            if (!node.Path.EndsWith(ownSegment, StringComparison.Ordinal))
            {
                pathMismatches.Add(new CheckSample($"HierarchyID={node.HierarchyId},MemberID={node.MemberId} Path='{node.Path}'"));
                continue;
            }

            var segmentCount = node.Path.Split('.', StringSplitOptions.RemoveEmptyEntries).Length;
            if (segmentCount != node.Level)
            {
                pathMismatches.Add(new CheckSample($"HierarchyID={node.HierarchyId},MemberID={node.MemberId} Level={node.Level} segments={segmentCount}"));
            }
        }
        sw.Stop();
        yield return CheckResult.FromViolationCount("A-HIE-04", ValidationPlane.A, ValidationLayer.Critical, "mHierarchyNode", "Path ends with the node's own MemberID, dot separator, Level = number of segments", nodes.Count, pathMismatches.Count, sw.ElapsedMilliseconds, pathMismatches);

        // I-TRE-06: STRONG form of Path: Path = Path(parent) + MemberID + '.', not only the suffix
        // (A-HIE-04 only checks that it ends there and that the number of segments matches).
        // Measured 0/18,390 (DPM 1.0), 0/16,538 (DPM 2.0 4.2), 0/18,825 (DPM 2.0 4.3): it holds on
        // the three own outputs, so it is registered as critical.
        sw.Restart();
        var strongPathMismatches = new List<CheckSample>();
        foreach (var node in nodes)
        {
            var parentPath = node.ParentMemberId is { } parentIdForPath && byKey.TryGetValue((node.HierarchyId, parentIdForPath), out var parentForPath)
                ? parentForPath.Path ?? string.Empty
                : string.Empty;
            var expectedPath = parentPath + node.MemberId + ".";
            if (node.Path != expectedPath)
            {
                strongPathMismatches.Add(new CheckSample($"HierarchyID={node.HierarchyId},MemberID={node.MemberId} Path='{node.Path}' expected='{expectedPath}'"));
            }
        }
        sw.Stop();
        yield return CheckResult.FromViolationCount("I-TRE-06", ValidationPlane.A, ValidationLayer.Critical, "mHierarchyNode", "Path = Path(parent) + MemberID + '.' (strong form, stricter than A-HIE-04)", nodes.Count, strongPathMismatches.Count, sw.ElapsedMilliseconds, strongPathMismatches);

        // A-HIE-05: (HierarchyID, MemberID) unique (guaranteed by the PK, corroborated); Order unique among siblings
        sw.Restart();
        var duplicateOrder = SqlHelpers.Scalar(
            c,
            """
            SELECT COUNT(*) FROM (
                SELECT "HierarchyID", "ParentMemberID", "Order" FROM "mHierarchyNode"
                WHERE "ParentMemberID" IS NOT NULL
                GROUP BY "HierarchyID", "ParentMemberID", "Order" HAVING COUNT(*) > 1)
            """);
        sw.Stop();
        yield return CheckResult.FromViolationCount("A-HIE-05", ValidationPlane.A, ValidationLayer.Critical, "mHierarchyNode", "(HierarchyID,MemberID) unique; Order unique among siblings", nodes.Count, duplicateOrder, sw.ElapsedMilliseconds, []);

        // A-HIE-06: every member of a node belongs to the domain of its hierarchy.
        // In DPM 2.0 this is I-TRE-07b: a hierarchy of a UNION DOMAIN (mDomainUnion) contains items
        // of its COMPONENTS, so the literal statement is violated by 994 nodes of the 4.2 and 39 of
        // the 4.0 (0 in the 3.2, which has no mDomainUnion). The form that survives: the domain of
        // the member is that of the hierarchy OR a domain UNITED to it.
        if (model == ValidationSourceModel.Dpm2)
        {
            yield return WrongDomainMembersExceptKnownAnomaly(c, exceptionSink);
        }
        else
        {
            sw.Restart();
            var wrongDomainDpm1 = SqlHelpers.Scalar(
                c,
                """
                SELECT COUNT(*) FROM "mHierarchyNode" n
                JOIN "mHierarchy" h ON h."HierarchyID" = n."HierarchyID"
                JOIN "mMember" m    ON m."MemberID" = n."MemberID"
                WHERE m."DomainID" <> h."DomainID"
                """);
            sw.Stop();
            yield return CheckResult.FromViolationCount("A-HIE-06", ValidationPlane.A, ValidationLayer.Critical, "mHierarchyNode", "Every member of a node belongs to the domain of its hierarchy", nodes.Count, wrongDomainDpm1, sw.ElapsedMilliseconds, []);
        }

        // A-HIE-07: ConceptID shared by more than one row (informative: declared, not corrected)
        sw.Restart();
        var shared = SqlHelpers.Scalar(
            c,
            """
            SELECT COUNT(*) FROM (
                SELECT "ConceptID" FROM "mHierarchyNode" WHERE "ConceptID" IS NOT NULL
                GROUP BY "ConceptID" HAVING COUNT(*) > 1)
            """);
        sw.Stop();
        yield return CheckResult.FromViolationCount("A-HIE-07", ValidationPlane.A, ValidationLayer.Informative, "mHierarchyNode", "ConceptID shared by several rows (declared, not corrected)", nodes.Count, shared, sw.ElapsedMilliseconds, []);
    }

    /// <summary>
    /// The 104 nodes of <c>I-TRE-07b</c> that remain after anchoring the 33 of <c>GA30</c>
    /// (<c>Dpm20HierarchyNodeLoader.ResolveMemberId</c>). The source asserts two incompatible things
    /// at once (an item re-versioned exactly at the release cut, and the subcategory that does not
    /// follow it) and it is not for us to arbitrate: they are declared, not relaxed.
    ///
    /// Shape of the key: <c>HierarchyCode:DomainCode:MemberCode</c> of the NODE AND THE RESOLVED
    /// MEMBER. Not <c>MemberID</c> (synthetic: a renumbering breaks it without the fact changing)
    /// nor <c>HierarchyCode</c> alone (it would survive, but a NEW and DIFFERENT case falling in
    /// the same hierarchy would be masked by the same entry). The chosen form sits exactly between
    /// the two: it survives a renumbering (business codes, not IDs) and masks nothing that is not
    /// THIS exact violation.
    ///
    /// Deliberate asymmetry with the other one-sided anomaly: here ONLY undeclared violations
    /// count as a failure (growth). A declared one that stops violating (shrinkage) is NOT counted:
    /// this anomaly is specific to the 4.3 release cut, not a permanent property of a single fixed
    /// Access. A 4.2, a future 4.4, or any output without this defect would have the 104 "absent"
    /// by design, and counting that as a failure would break --validate on all of them. Growth
    /// still protects: 105 cases, or 104 different ones, fail all the same. The keys live in the
    /// single <see cref="KnownExceptions"/> registry (<c>Sidedness.OneSided</c>).
    /// </summary>
    private static CheckResult WrongDomainMembersExceptKnownAnomaly(SqliteConnection c, List<KnownExceptions.Outcome> exceptionSink)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var total = SqlHelpers.Scalar(c, "SELECT COUNT(*) FROM \"mHierarchyNode\"");
        var rows = SqlHelpers.Rows(
            c,
            """
            SELECT h."HierarchyCode" || ':' || d."DomainCode" || ':' || m."MemberCode"
            FROM "mHierarchyNode" n
            JOIN "mHierarchy" h ON h."HierarchyID" = n."HierarchyID"
            JOIN "mMember" m    ON m."MemberID" = n."MemberID"
            JOIN "mDomain" d    ON d."DomainID" = m."DomainID"
            WHERE m."DomainID" <> h."DomainID"
              AND NOT EXISTS (
                  SELECT 1 FROM "mDomainUnion" du
                  WHERE du."UnionDomainID" = h."DomainID" AND du."UnitedDomainID" = m."DomainID")
            """,
            1);
        sw.Stop();

        var violating = rows.Select(r => r[0]!).ToHashSet(StringComparer.Ordinal);
        var (unresolved, outcomes) = KnownExceptions.ApplyOriginAnomalyExceptions("I-TRE-07b", violating, ValidationPlane.A);
        exceptionSink.AddRange(outcomes);
        var unexpected = unresolved.Select(k => new CheckSample(k)).ToList();

        return CheckResult.FromViolationCount(
            "I-TRE-07b", ValidationPlane.A, ValidationLayer.Critical, "mHierarchyNode",
            "The member of a node belongs to the domain of its hierarchy, to that of a domain united to it, or is one of the 104 declared anomalies (named exception)",
            total, unexpected.Count, sw.ElapsedMilliseconds, unexpected);
    }

    private static CheckResult ExactMatch(SqliteConnection c, string id, string table, string statement, string countSql, long expected) =>
        SqlHelpers.ExactMatch(c, id, ValidationLayer.Critical, table, statement, countSql, expected);

    private static CheckResult CountsEqual(SqliteConnection c, string id, string table, string statement, string leftSql, string rightSql) =>
        SqlHelpers.CountsEqual(c, id, ValidationLayer.Critical, table, statement, leftSql, rightSql);
}
