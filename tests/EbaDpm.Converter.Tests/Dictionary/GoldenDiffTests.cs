using System.Text.RegularExpressions;

namespace EbaDpm.Converter.Tests.Dictionary;

/// <summary>
/// "Golden" level -- first semantic diff of the generated dictionary against
/// <c>EBA_3.2_phase_1.db</c>, the primary reference (1:1 coverage with the Access database and
/// pure DPM 1.0). Critical layer: domain, member, dimension and metric are compared by code, and
/// the generated output must CONTAIN the reference (never less).
///
/// XBRL codes are compared normalising the release suffix on both sides. The output almost never
/// carries a suffix (it is verbatim from the Access, which barely suffixes dimensions and never
/// members) and the 3.2 reference does not suffix anything either, so the normalisation must be
/// harmless: the tests check this explicitly by also comparing WITHOUT normalising.
///
/// Four objects of the 3.2 reference (`met_temp` with its 3 members, `templateDomain`,
/// `template`, `filed`) are registered exceptions, applied here one by one by business key, never
/// as a general rule. A fifth missing object still makes the test fail. If any of them
/// disappeared entirely from the reference, <see cref="AssertContainsReference"/> flags it as
/// stale and fails. Two objects that looked like the same pattern were NOT: the `MET` domain is
/// the Access `AT` <c>Domain</c> renamed; the `MET` dimension has no source in the Access but is in
/// all THREE references, including 3.2 (pure DPM 1.0): it is a structural piece of the format
/// that the converter emits, not a defect of the reference.
/// </summary>
[Collection("Dictionary")]
public sealed class GoldenDiffTests
{
    private readonly DictionaryFixture _fixture;

    public GoldenDiffTests(DictionaryFixture fixture)
    {
        _fixture = fixture;
    }

    private static readonly Regex ReleaseSuffixPattern = new(@"_(\d+(?:\.\d+)*)(?=:)", RegexOptions.Compiled);

    /// <summary>Symmetric normalisation of the release suffix in an XBRL code.</summary>
    private static string NormalizeXbrlCode(string code) => ReleaseSuffixPattern.Replace(code, string.Empty);

    // ------------------------------------------------------------------
    // Exceptions named by business key, one per comparison. "Everything Technical_*" is never
    // excluded; only these four exact codes.
    // ------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, string> DomainCodeExceptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["met_temp"] = "temporary metrics domain",
        ["templateDomain"] = "template domain",
    };

    private static readonly IReadOnlyDictionary<string, string> DimensionCodeExceptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["template"] = "template dimension",
    };

    private static readonly IReadOnlyDictionary<string, string> MemberCodeExceptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["filed"] = "filed member",
    };

    private static readonly IReadOnlyDictionary<string, string> DimensionXbrlCodeExceptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Technical_dim:template"] = "template dimension",
    };

    private static readonly IReadOnlyDictionary<string, string> MemberXbrlCodeExceptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Technical_met:filed"] = "filed member",
        // Scope extension of the temporary metrics domain exception: the 3 members of the
        // already excluded `met_temp` domain itself. Named one by one; a fourth MemberXBRLCode
        // "eba_met_temp:*" that is not exactly one of these three must still fail (the guard is
        // not a pattern).
        ["eba_met_temp:si288"] = "temporary metrics domain",
        ["eba_met_temp:si730"] = "temporary metrics domain",
        ["eba_met_temp:si731"] = "temporary metrics domain",
    };

    private HashSet<string> GeneratedCodes(string sql) =>
        QueryHelpers.Rows(_fixture.GeneratedConnection, sql, 1)
            .Select(r => r[0])
            .Where(v => v is not null)
            .Select(v => v!)
            .ToHashSet(StringComparer.Ordinal);

    private HashSet<string> Reference32Codes(string sql) =>
        QueryHelpers.Rows(_fixture.Reference32Connection, sql, 1)
            .Select(r => r[0])
            .Where(v => v is not null)
            .Select(v => v!)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Coverage (the generated output contains the reference), with exceptions named by business
    /// key. The criterion is never relaxed for anyone other than the exact objects of
    /// <paramref name="exceptions"/>.
    /// </summary>
    private static void AssertContainsReference(
        string label,
        HashSet<string> generated,
        HashSet<string> reference,
        IReadOnlyDictionary<string, string>? exceptions = null)
    {
        exceptions ??= new Dictionary<string, string>(StringComparer.Ordinal);

        // The compared census must be CHECKED, not just printed in the failure message. If the
        // reference query broke (a JOIN that does not match, an extra filter, an empty table) and
        // returned 0 rows, "missing.Count == 0" would pass green without having compared
        // anything -- the same false-green mechanism as the NULL-unsafe `<>`, applied to sets.
        Assert.True(reference.Count > 0, $"{label}: the reference query returned 0 rows; the coverage comparison would be empty.");

        // An exception that no longer corresponds to ANY object of the reference makes the test
        // FAIL -- it would be a stale exception, and letting it pass silently is as dangerous as
        // not running the test.
        var staleExceptions = exceptions.Keys.Where(code => !reference.Contains(code)).ToList();
        Assert.True(
            staleExceptions.Count == 0,
            $"{label}: {staleExceptions.Count} STALE exception(s) - they no longer correspond to "
            + "any object of the 3.2 reference and must be removed from the list of known "
            + $"reference defects: {string.Join(", ", staleExceptions.Select(c => $"{c} ({exceptions[c]})"))}");

        var missingRaw = reference.Except(generated, StringComparer.Ordinal).ToList();
        var appliedExceptions = missingRaw.Where(exceptions.ContainsKey).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var missing = missingRaw.Except(exceptions.Keys, StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var extra = generated.Except(reference, StringComparer.Ordinal).Count();

        // The report ALWAYS lists the applied exceptions, never silently.
        var exceptionsNote = appliedExceptions.Count == 0
            ? "(none)"
            : string.Join(", ", appliedExceptions.Select(c => $"{c} ({exceptions[c]})"));
        Console.WriteLine($"{label}: exceptions applied: {exceptionsNote}.");

        Assert.True(
            missing.Count == 0,
            $"{label}: {missing.Count} codes of the 3.2 reference are not in the generated output "
            + "(coverage is mandatory) and have NO registered exception. Examples: "
            + $"{string.Join(", ", missing.Take(15))}. (Generated: {generated.Count}, reference: "
            + $"{reference.Count}, expected and legitimate excess: {extra}, exceptions applied: {exceptionsNote}.)");
    }

    // ------------------------------------------------------------------
    // Business codes: domain, member, dimension, metric
    // ------------------------------------------------------------------

    [DataFact]
    public void DomainCodes_GeneratedContainsReference32()
    {
        var generated = GeneratedCodes("SELECT \"DomainCode\" FROM \"mDomain\"");
        var reference = Reference32Codes("SELECT \"DomainCode\" FROM \"mDomain\"");
        AssertContainsReference("mDomain.DomainCode", generated, reference, DomainCodeExceptions);
    }

    [DataFact]
    public void MemberCodes_GeneratedContainsReference32()
    {
        var generated = GeneratedCodes("SELECT \"MemberCode\" FROM \"mMember\"");
        var reference = Reference32Codes("SELECT \"MemberCode\" FROM \"mMember\"");
        AssertContainsReference("mMember.MemberCode", generated, reference, MemberCodeExceptions);
    }

    /// <summary>
    /// The dimension <c>DimensionCode='MET'</c> (3.2 <c>DimensionID=365</c>, 4.0/4.2 both
    /// <c>DimensionID=9999</c>, same business key in all three) has no source in the Access under
    /// that code (it is the renamed <c>ATY</c> dimension), but is in all THREE references,
    /// including 3.2 (pure DPM 1.0): it is not a defect of the reference but a structural piece
    /// of the output format, like the two constant rows of <c>mOwner</c>. It is verified in detail
    /// in <see cref="MetDimensionTests"/>: here it is enough that its census enters the generated
    /// output.
    /// </summary>
    [DataFact]
    public void DimensionCodes_GeneratedContainsReference32()
    {
        var generated = GeneratedCodes("SELECT \"DimensionCode\" FROM \"mDimension\"");
        var reference = Reference32Codes("SELECT \"DimensionCode\" FROM \"mDimension\"");
        AssertContainsReference("mDimension.DimensionCode", generated, reference, DimensionCodeExceptions);
    }

    [DataFact]
    public void MetricCodes_GeneratedContainsReference32()
    {
        // mMetric has no code column of its own (see the destination schema): the business code
        // of a metric is the MemberCode of the corresponding member
        // (MetricID = CorrespondingMemberID = Member.MemberID). The member 'filed' is also a
        // metric in the 3.2 reference, so the same exception applies here.
        var generated = GeneratedCodes(
            """
            SELECT m."MemberCode" FROM "mMetric" met
            JOIN "mMember" m ON m."MemberID" = met."CorrespondingMemberID"
            """);
        var reference = Reference32Codes(
            """
            SELECT m."MemberCode" FROM "mMetric" met
            JOIN "mMember" m ON m."MemberID" = met."CorrespondingMemberID"
            """);
        AssertContainsReference("mMetric (via MemberCode of the CorrespondingMemberID)", generated, reference, MemberCodeExceptions);
    }

    // ------------------------------------------------------------------
    // XBRL codes, with release-suffix normalisation (checked to be harmless)
    // ------------------------------------------------------------------

    /// <summary>
    /// The same `MET` dimension documented in <see cref="DimensionCodes_GeneratedContainsReference32"/>,
    /// seen here in its XBRL code form (<c>DimensionXBRLCode='MET'</c>, no namespace).
    /// </summary>
    [DataFact]
    public void DimensionXbrlCodes_GeneratedContainsReference32_WithReleaseSuffixNormalization()
    {
        var generatedRaw = GeneratedCodes("SELECT \"DimensionXBRLCode\" FROM \"mDimension\" WHERE \"DimensionXBRLCode\" IS NOT NULL");
        var referenceRaw = Reference32Codes("SELECT \"DimensionXBRLCode\" FROM \"mDimension\" WHERE \"DimensionXBRLCode\" IS NOT NULL");

        var generatedNormalized = generatedRaw.Select(NormalizeXbrlCode).ToHashSet(StringComparer.Ordinal);
        var referenceNormalized = referenceRaw.Select(NormalizeXbrlCode).ToHashSet(StringComparer.Ordinal);
        AssertContainsReference(
            "mDimension.DimensionXBRLCode (normalized)", generatedNormalized, referenceNormalized, DimensionXbrlCodeExceptions);

        // The normalisation must be harmless: we check that it also holds WITHOUT normalising
        // (with the same template dimension exception), and therefore that it is not masking any
        // real suffix difference.
        var missingWithoutNormalization = referenceRaw.Except(generatedRaw, StringComparer.Ordinal)
            .Except(DimensionXbrlCodeExceptions.Keys, StringComparer.Ordinal)
            .ToList();
        Assert.True(
            missingWithoutNormalization.Count == 0,
            "The normalisation should not be necessary, but WITHOUT normalising there are "
            + $"{missingWithoutNormalization.Count} codes of the 3.2 reference missing from the generated output "
            + $"(discounting the template dimension exception): {string.Join(", ", missingWithoutNormalization.Take(15))}. "
            + "This indicates that the normalisation is still needed (or that there is a regression), contrary to expectation.");
    }

    /// <summary>
    /// 3 codes (<c>eba_met_temp:si288</c>, <c>eba_met_temp:si730</c>, <c>eba_met_temp:si731</c>)
    /// are missing ONLY in this comparison, not in <see cref="MemberCodes_GeneratedContainsReference32"/>,
    /// because the 3.2 reference has TWO rows for each <c>MemberCode</c>: one under the already
    /// excluded `met_temp` domain and another under the `MET` domain. The Access only has the
    /// second. They are the members of `met_temp` itself, covered by the scope extension of the
    /// temporary metrics domain exception, named one by one in
    /// <see cref="MemberXbrlCodeExceptions"/>: a fourth <c>eba_met_temp:*</c> that is not exactly
    /// one of these three must still fail.
    /// </summary>
    [DataFact]
    public void MemberXbrlCodes_GeneratedContainsReference32_WithReleaseSuffixNormalization()
    {
        var generatedRaw = GeneratedCodes("SELECT \"MemberXBRLCode\" FROM \"mMember\" WHERE \"MemberXBRLCode\" IS NOT NULL");
        var referenceRaw = Reference32Codes("SELECT \"MemberXBRLCode\" FROM \"mMember\" WHERE \"MemberXBRLCode\" IS NOT NULL");

        var generatedNormalized = generatedRaw.Select(NormalizeXbrlCode).ToHashSet(StringComparer.Ordinal);
        var referenceNormalized = referenceRaw.Select(NormalizeXbrlCode).ToHashSet(StringComparer.Ordinal);
        AssertContainsReference(
            "mMember.MemberXBRLCode (normalized)", generatedNormalized, referenceNormalized, MemberXbrlCodeExceptions);

        var missingWithoutNormalization = referenceRaw.Except(generatedRaw, StringComparer.Ordinal)
            .Except(MemberXbrlCodeExceptions.Keys, StringComparer.Ordinal)
            .ToList();
        Assert.True(
            missingWithoutNormalization.Count == 0,
            "The normalisation should not be necessary, but WITHOUT normalising there are "
            + $"{missingWithoutNormalization.Count} codes of the 3.2 reference missing from the generated output "
            + $"(discounting the filed member and temporary metrics domain exceptions): {string.Join(", ", missingWithoutNormalization.Take(15))}. "
            + "This indicates that the normalisation is still needed (or that there is a regression), contrary to expectation.");
    }
}
