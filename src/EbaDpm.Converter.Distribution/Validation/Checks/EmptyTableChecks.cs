using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Distribution.Validation.Checks;

/// <summary>
/// Tables that must remain empty. Plane A, without looking at any reference.
///
/// Four of the 18 tables of the DPM 1.0 source are populated by DPM 2.0:
/// <c>mDomainUnion</c>, <c>mRewriteURI</c>, <c>mTaxonomyPackage</c> and
/// <c>aDatabaseProperties</c> (now 2 rows, not 1), so on a DPM 2.0 output they stop requiring
/// emptiness. The remaining 14 still require 0 rows in both models: the change is by SCOPE, the
/// DPM 1.0 form is untouched.
///
/// Of those four, ONLY <c>mDomainUnion</c> comes 1:1 from the source:
/// <c>Dpm20DictionaryLoader</c> writes one row per domain-union relation of the Access, and that
/// count VARIES by release (102 in 4.2, 104 in 4.3, measured). The other three
/// (<c>mRewriteURI</c>=4, <c>mTaxonomyPackage</c>=1, <c>aDatabaseProperties</c>=2) are written by
/// <c>Dpm20SkeletonLoader</c> with LITERAL rows fixed in code: they do not derive from the Access,
/// so their census IS a stable property of the pipeline, not a measurement of one publication,
/// and they are still checked by exact census. <c>mDomainUnion</c> becomes an INFORMATIVE census:
/// "not empty" was a measurement on 4.2/4.3, not a property of the format (no row starts before
/// release 4.0), so it states the count and never fails.
/// </summary>
public static class EmptyTableChecks
{
    /// <summary>The 14 that stay empty in both models.</summary>
    private static readonly string[] AlwaysEmpty =
    [
        "vValidationRuleExpressions", "vValidationRuleTables",
        "mConceptReference", "mReference", "mReferencePart", "mReferenceValue",
        "mNamespacePrefix", "mResourceFile", "mXbrlExportConfiguration",
        "aContainerInfo", "aDDSInfo",
        "dInstance", "dFilingIndicator",
        "mCustomDataType",
    ];

    /// <summary>
    /// The 3 that DPM 1.0 leaves empty and DPM 2.0 populates with LITERAL pipeline rows: a stable
    /// census, not derived from the Access, safe as an exact number.
    /// </summary>
    private static readonly (string Table, long Dpm2Census)[] Dpm2PopulatedWithFixedCensus =
    [
        ("mRewriteURI", 4),
        ("mTaxonomyPackage", 1),
        ("aDatabaseProperties", 2), // "Validation syntax version" + "Source model"
    ];

    public static IEnumerable<CheckResult> Run(SqliteConnection c, ValidationSourceModel model)
    {
        foreach (var table in AlwaysEmpty)
        {
            yield return SqlHelpers.ExactMatch(
                c, $"A-VAC-01.{table}", ValidationLayer.Critical, table, $"{table} is empty",
                $"SELECT COUNT(*) FROM \"{table}\"", 0);
        }

        foreach (var (table, dpm2Census) in Dpm2PopulatedWithFixedCensus)
        {
            var expected = model == ValidationSourceModel.Dpm2 ? dpm2Census : 0;
            var statement = model == ValidationSourceModel.Dpm2
                ? $"{table} has exactly {expected} row(s) (populated by DPM 2.0, LITERAL pipeline census)"
                : $"{table} is empty";
            yield return SqlHelpers.ExactMatch(
                c, $"A-VAC-01.{table}", ValidationLayer.Critical, table, statement,
                $"SELECT COUNT(*) FROM \"{table}\"", expected);
        }

        // mDomainUnion: 1:1 from the source, its census VARIES by release and is 0 in releases
        // before 4.0. DPM 2.0: informative census; DPM 1.0: empty.
        yield return DomainUnionNotEmptyInDpm2(c, model);
    }

    private static CheckResult DomainUnionNotEmptyInDpm2(SqliteConnection c, ValidationSourceModel model)
    {
        var countSql = "SELECT COUNT(*) FROM \"mDomainUnion\"";
        if (model == ValidationSourceModel.Dpm2)
        {
            // Informative census: it comes 1:1 from SuperCategoryComposition, which has no row
            // before release 4.0, so older releases legitimately have 0 rows. States the count,
            // never fails.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var count = SqlHelpers.Scalar(c, countSql);
            sw.Stop();
            return CheckResult.FromViolationCount(
                "A-VAC-01.mDomainUnion", ValidationPlane.A, ValidationLayer.Informative, "mDomainUnion",
                "mDomainUnion census (DPM 2.0, 1:1 from the source domain-union relations; the count depends on the release and may legitimately be 0)",
                examined: 1, failed: 0, sw.ElapsedMilliseconds, [new CheckSample($"mDomainUnion has {count} row(s)")]);
        }

        return SqlHelpers.ExactMatch(
            c, "A-VAC-01.mDomainUnion", ValidationLayer.Critical, "mDomainUnion", "mDomainUnion is empty",
            countSql, 0);
    }
}
