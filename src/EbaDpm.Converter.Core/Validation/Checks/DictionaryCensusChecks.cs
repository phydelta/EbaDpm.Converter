using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.Checks;

/// <summary>
/// B-DIC-01: censuses of <c>mDomain</c>, <c>mMember</c>, <c>mDimension</c>, <c>mMetric</c> by code
/// and by XBRL code, against any of the three references (the report declares the role of each).
/// Known exceptions live in the single exception registry.
///
/// The dictionary is a GLOBAL COMMON BASE: it does not belong to any framework and is not
/// versioned with it. For that reason <see cref="Run"/> NEVER filters by the universe of
/// comparable taxonomies (unlike the table checks): it compares ALL of
/// <c>mDomain</c>/<c>mMember</c>/<c>mDimension</c>/<c>mMetric</c>/<c>mHierarchy</c> of the generated
/// output against ALL of the reference's, without narrowing.
/// </summary>
public static class DictionaryCensusChecks
{
    /// <summary>The 9 checks of this family, so that ALL of them can be skipped for the same reason.</summary>
    private static readonly (string Id, string Table)[] AllCensusChecks =
    [
        ("B-DIC-01.1", "mDomain"), ("B-DIC-01.2", "mMember"), ("B-DIC-01.3", "mDimension"),
        ("B-DIC-01.4", "mMetric"), ("B-DIC-01.5", "mDomain"), ("B-DIC-01.6", "mDimension"),
        ("B-DIC-01.7", "mMember"), ("B-DIC-01.8", "mHierarchy"), ("B-DIC-01.9", "mMember"),
    ];

    public static IEnumerable<CheckResult> Run(SqliteConnection generated, SqliteConnection reference, string referenceRole, List<KnownExceptions.Outcome> exceptionSink)
    {
        // The WHOLE family compares the global common base, so ALL of it shares the same risk, not
        // only B-DIC-01.8. A generated dictionary from a later publication (e.g. 4.3) HAS ALREADY
        // moved beyond what a reference from an earlier publication (e.g. 4.2) can know: comparing
        // DomainCode, MemberCode, DimensionCode or the XBRL codes across different publications is
        // as structurally invalid as comparing hierarchies. The guard is evaluated ONCE, here, and
        // skips all 9 checks so that none of them compares silently.
        var publicationMismatch = PublicationMismatchReason(generated, referenceRole);
        if (publicationMismatch is not null)
        {
            foreach (var (id, table) in AllCensusChecks)
            {
                yield return CheckResult.Skip(id, ValidationPlane.B, ValidationLayer.Critical, table, CensusStatement(id), publicationMismatch);
            }

            yield break;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var generatedDomain = PlaneBSupport.Codes(generated, "SELECT \"DomainCode\" FROM \"mDomain\"");
        var referenceDomain = PlaneBSupport.Codes(reference, "SELECT \"DomainCode\" FROM \"mDomain\"");
        yield return PlaneBSupport.Containment(
            "B-DIC-01.1", ValidationLayer.Critical, "mDomain", "Census of DomainCode: generated contains the reference",
            "B-DIC-01-DOMAIN-CODE", referenceRole, generatedDomain, referenceDomain, exceptionSink, sw.ElapsedMilliseconds);

        sw.Restart();
        var generatedMember = PlaneBSupport.Codes(generated, "SELECT \"MemberCode\" FROM \"mMember\"");
        var referenceMember = PlaneBSupport.Codes(reference, "SELECT \"MemberCode\" FROM \"mMember\"");
        yield return PlaneBSupport.Containment(
            "B-DIC-01.2", ValidationLayer.Critical, "mMember", "Census of MemberCode: generated contains the reference",
            "B-DIC-01-MEMBER-CODE", referenceRole, generatedMember, referenceMember, exceptionSink, sw.ElapsedMilliseconds);

        sw.Restart();
        var generatedDimension = PlaneBSupport.Codes(generated, "SELECT \"DimensionCode\" FROM \"mDimension\"");
        var referenceDimension = PlaneBSupport.Codes(reference, "SELECT \"DimensionCode\" FROM \"mDimension\"");
        yield return PlaneBSupport.Containment(
            "B-DIC-01.3", ValidationLayer.Critical, "mDimension", "Census of DimensionCode: generated contains the reference",
            "B-DIC-01-DIMENSION-CODE", referenceRole, generatedDimension, referenceDimension, exceptionSink, sw.ElapsedMilliseconds);

        sw.Restart();
        const string metricSql =
            """
            SELECT m."MemberCode" FROM "mMetric" met JOIN "mMember" m ON m."MemberID" = met."CorrespondingMemberID"
            """;
        var generatedMetric = PlaneBSupport.Codes(generated, metricSql);
        var referenceMetric = PlaneBSupport.Codes(reference, metricSql);
        yield return PlaneBSupport.Containment(
            "B-DIC-01.4", ValidationLayer.Critical, "mMetric", "Census of mMetric (via MemberCode of CorrespondingMemberID): generated contains the reference",
            "B-DIC-01-METRIC-CODE", referenceRole, generatedMetric, referenceMetric, exceptionSink, sw.ElapsedMilliseconds);

        // ---- XBRL codes, normalised to the local part ----
        sw.Restart();
        var generatedDomainXbrl = LocalParts(generated, "SELECT \"DomainXBRLCode\" FROM \"mDomain\" WHERE \"DomainXBRLCode\" IS NOT NULL");
        var referenceDomainXbrl = LocalParts(reference, "SELECT \"DomainXBRLCode\" FROM \"mDomain\" WHERE \"DomainXBRLCode\" IS NOT NULL");
        yield return PlaneBSupport.Containment(
            "B-DIC-01.5", ValidationLayer.Critical, "mDomain", "Census of DomainXBRLCode (local part): generated contains the reference",
            "B-DIC-01-DOMAIN-XBRL", referenceRole, generatedDomainXbrl, referenceDomainXbrl, exceptionSink, sw.ElapsedMilliseconds);

        sw.Restart();
        var generatedDimensionXbrl = LocalParts(generated, "SELECT \"DimensionXBRLCode\" FROM \"mDimension\" WHERE \"DimensionXBRLCode\" IS NOT NULL");
        var referenceDimensionXbrl = LocalParts(reference, "SELECT \"DimensionXBRLCode\" FROM \"mDimension\" WHERE \"DimensionXBRLCode\" IS NOT NULL");
        yield return PlaneBSupport.Containment(
            "B-DIC-01.6", ValidationLayer.Critical, "mDimension", "Census of DimensionXBRLCode (local part): generated contains the reference",
            "B-DIC-01-DIMENSION-XBRL", referenceRole, generatedDimensionXbrl, referenceDimensionXbrl, exceptionSink, sw.ElapsedMilliseconds);

        sw.Restart();
        var generatedMemberXbrl = LocalParts(generated, "SELECT \"MemberXBRLCode\" FROM \"mMember\" WHERE \"MemberXBRLCode\" IS NOT NULL");
        var referenceMemberXbrl = LocalParts(reference, "SELECT \"MemberXBRLCode\" FROM \"mMember\" WHERE \"MemberXBRLCode\" IS NOT NULL");
        yield return PlaneBSupport.Containment(
            "B-DIC-01.7", ValidationLayer.Critical, "mMember", "Census of MemberXBRLCode (local part): generated contains the reference",
            "B-DIC-01-MEMBER-XBRL", referenceRole, generatedMemberXbrl, referenceMemberXbrl, exceptionSink, sw.ElapsedMilliseconds);

        // ---- B-DIC-01.8: census of mHierarchy by domain:code, the hook for the declared
        // divergence of 59 hierarchies. ----
        sw.Restart();
        yield return HierarchyCensus(generated, reference, referenceRole, exceptionSink, sw);

        // ---- B-DIC-01.9: GeneratedCensus mode, the hook for the qIO members exception. ----
        sw.Restart();
        yield return QioMemberGeneratedCensus(generated, reference, referenceRole, exceptionSink, sw);
    }

    /// <summary>
    /// Evaluated ONCE for the whole family. The risk of comparing the dictionary across different
    /// publications is not specific to <c>B-DIC-01.8</c>: it affects the entire GLOBAL COMMON BASE.
    /// Returns the reason for skipping, or <c>null</c> if the two publications match (or the
    /// publication of the generated output cannot be determined, as in DPM 1.0, which has no such
    /// single cut).
    ///
    /// The reference declares its publication through the file NAME (<see cref="ReferenceRole"/>).
    /// The generated output declares it by reading <c>mRelease.IsCurrent</c>: DPM 2.0 has EXACTLY
    /// one current row (the cut of THAT conversion, "4.2" or "4.3"; converting the 4.3 Access at
    /// the 4.3 cut yields the dictionary in 4.3 STATE, which has already moved beyond what a 4.2
    /// reference can know); DPM 1.0 has several <c>IsCurrent=1</c> at once (it is not versioned by
    /// cut) and the condition does not apply: it keeps comparing as usual.
    /// </summary>
    private static string? PublicationMismatchReason(SqliteConnection generated, string referenceRole)
    {
        var generatedCurrentReleases = SqlHelpers.Rows(generated, "SELECT \"ReleaseCode\" FROM \"mRelease\" WHERE \"IsCurrent\" = 1", 1).Select(r => r[0]).ToList();
        var generatedPublication = generatedCurrentReleases.Count == 1 ? generatedCurrentReleases[0] : null; // DPM 1.0: no single cut, no gate.
        var referencePublication = referenceRole switch
        {
            ReferenceRole.Reference42 => "4.2",
            ReferenceRole.Reference40 => "4.0",
            ReferenceRole.Reference32 => "3.2",
            _ => null,
        };

        if (generatedPublication is null || referencePublication is null || generatedPublication == referencePublication)
        {
            return null;
        }

        return $"Publication mismatch: the generated output is from publication {generatedPublication} and the reference from {referencePublication}. The dictionary " +
               "(global common base) has already moved on, and comparing across different publications is structurally invalid, not a stricter check.";
    }

    private static string CensusStatement(string id) => id switch
    {
        "B-DIC-01.1" => "Census of DomainCode: generated contains the reference",
        "B-DIC-01.2" => "Census of MemberCode: generated contains the reference",
        "B-DIC-01.3" => "Census of DimensionCode: generated contains the reference",
        "B-DIC-01.4" => "Census of mMetric (via MemberCode of CorrespondingMemberID): generated contains the reference",
        "B-DIC-01.5" => "Census of DomainXBRLCode (local part): generated contains the reference",
        "B-DIC-01.6" => "Census of DimensionXBRLCode (local part): generated contains the reference",
        "B-DIC-01.7" => "Census of MemberXBRLCode (local part): generated contains the reference",
        "B-DIC-01.8" => "Census of mHierarchy by DomainCode:HierarchyCode: generated contains the reference, except the 59 declared divergences (DPM 1.0 leftover in the 4.2 reference)",
        "B-DIC-01.9" => "GeneratedCensus: the 109 qIO members of the declared exception are still emitted in the generated output, and still absent from the reference",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unrecognised B-DIC-01.* id"),
    };

    /// <summary>
    /// B-DIC-01.9: <c>GeneratedCensus</c> mode for the qIO members exception, neither containment
    /// nor divergence. It verifies that the GENERATED output still contains the 109
    /// <c>MemberXBRLCode</c> of <c>qIO</c> (if it stops emitting any, that is a regression) AND that
    /// the REFERENCE still publishes none of them (if it starts to, the entry became obsolete). It
    /// deliberately does not filter by <c>DomainCode='qIO'</c> in SQL: the CHECK is domain-agnostic
    /// and the list of 109 keys lives in the registry, separating "what we check" from "what is
    /// declared".
    /// </summary>
    private static CheckResult QioMemberGeneratedCensus(SqliteConnection generated, SqliteConnection reference, string referenceRole,
        List<KnownExceptions.Outcome> exceptionSink, System.Diagnostics.Stopwatch sw)
    {
        const string id = "B-DIC-01.9";
        var statement = CensusStatement(id);

        var generatedXbrl = PlaneBSupport.Codes(generated, "SELECT \"MemberXBRLCode\" FROM \"mMember\" WHERE \"MemberXBRLCode\" IS NOT NULL");
        var referenceXbrl = PlaneBSupport.Codes(reference, "SELECT \"MemberXBRLCode\" FROM \"mMember\" WHERE \"MemberXBRLCode\" IS NOT NULL");
        sw.Stop();

        var (failing, outcomes) = KnownExceptions.ApplyGeneratedCensusExceptions(id, referenceRole, generatedXbrl, referenceXbrl, ValidationPlane.B);
        exceptionSink.AddRange(outcomes);
        var declaredCount = KnownExceptions.For(id, ValidationPlane.B).Count(e => e.Kind == ExceptionKind.Defect);
        var samples = failing.Select(f => new CheckSample(f)).ToList();

        return CheckResult.FromViolationCount(
            id, ValidationPlane.B, ValidationLayer.Critical, "mMember", statement,
            declaredCount, samples.Count, sw.ElapsedMilliseconds, samples);
    }

    /// <summary>
    /// B-DIC-01.8: census of <c>mHierarchy</c> by business key <c>DomainCode:HierarchyCode</c>
    /// (the same form in which the 59-hierarchy divergence is declared), by containment, CRITICAL
    /// layer. The check itself is critical; the 59 declared hierarchies are excluded by exact key,
    /// which does not relax the criterion but declares it. The publication guard was already
    /// evaluated in <see cref="Run"/> for the whole family.
    /// </summary>
    private static CheckResult HierarchyCensus(SqliteConnection generated, SqliteConnection reference, string referenceRole,
        List<KnownExceptions.Outcome> exceptionSink, System.Diagnostics.Stopwatch sw)
    {
        const string id = "B-DIC-01.8";
        const string table = "mHierarchy";
        var statement = CensusStatement(id);

        var generatedKeys = PlaneBSupport.Codes(generated, "SELECT d.\"DomainCode\" || ':' || h.\"HierarchyCode\" FROM \"mHierarchy\" h JOIN \"mDomain\" d ON d.\"DomainID\" = h.\"DomainID\"");
        var referenceKeys = PlaneBSupport.Codes(reference, "SELECT d.\"DomainCode\" || ':' || h.\"HierarchyCode\" FROM \"mHierarchy\" h JOIN \"mDomain\" d ON d.\"DomainID\" = h.\"DomainID\"");

        var missing = referenceKeys.Except(generatedKeys, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

        // The 59 declared hierarchies live in the single KnownExceptions registry; they are read
        // filtered by Check + EXACT Reference, never by pattern. This is a TWO-SIDED census, unlike
        // the release-boundary exception of I-TRE-07b: the divergence does NOT depend on the
        // release cut (0 in SubCategory of both 4.2 and 4.3), it is source-shaped, not
        // boundary-shaped (Sidedness.TwoSided in the registry). Both directions are therefore real
        // signal: "grows" (an undeclared key is missing) is a new violation; "shrinks" (one of the
        // 59 declared ones is NO LONGER missing because the mapping started to produce it) means
        // the exemption became obsolete and must be reviewed, not silently celebrated.
        var declaredExceptions = KnownExceptions.For(id, ValidationPlane.B)
            .Where(e => e.Kind == ExceptionKind.DeclaredDivergence && e.Sidedness == Sidedness.TwoSided)
            .ToList();
        var declared = declaredExceptions
            .Where(e => e.Reference == "*" || e.Reference == referenceRole)
            .Select(e => e.BusinessKey)
            .ToHashSet(StringComparer.Ordinal);

        // One outcome per exception: the divergence holds (Matched=1) while the declared key is in
        // the reference and absent from the generated output; otherwise the exception is stale.
        foreach (var exception in declaredExceptions)
        {
            if (!(exception.Reference == "*" || exception.Reference == referenceRole))
            {
                exceptionSink.Add(new KnownExceptions.Outcome(exception, false, 0, false));
                continue;
            }

            var holds = missing.Contains(exception.BusinessKey);
            exceptionSink.Add(new KnownExceptions.Outcome(exception, true, holds ? 1 : 0, !holds));
        }

        var unexpectedMissing = missing.Where(k => !declared.Contains(k))
            .Select(k => new CheckSample(k)).ToList();

        // "No longer missing" covers TWO distinct cases: either we now generate it, or the
        // reference no longer carries it (the reference changed, not our output). Without
        // distinguishing them, a change in the reference would read as "the exception became
        // obsolete because the mapping produces it", which would be false: nobody produces it,
        // there is nothing left to compare against. Both are signal (Sidedness.TwoSided), but the
        // message says which one it is.
        var noLongerApplicable = new List<CheckSample>();
        foreach (var key in declared)
        {
            if (missing.Contains(key))
            {
                continue; // still missing: the exception still holds, not a signal.
            }

            noLongerApplicable.Add(referenceKeys.Contains(key)
                ? new CheckSample($"(declared divergence obsolete: we now generate it) {key}")
                : new CheckSample($"(declared divergence: no longer in the reference, not because we generate it) {key}"));
        }

        var samples = unexpectedMissing.Concat(noLongerApplicable).ToList();
        sw.Stop();

        return CheckResult.FromViolationCount(
            id, ValidationPlane.B, ValidationLayer.Critical, table, statement,
            referenceKeys.Count, samples.Count, sw.ElapsedMilliseconds, samples);
    }

    private static HashSet<string> LocalParts(SqliteConnection connection, string sql) =>
        PlaneBSupport.Codes(connection, sql).Select(Normalization.XbrlLocalPart).ToHashSet(StringComparer.Ordinal);
}
