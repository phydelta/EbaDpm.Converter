using System.Reflection;

namespace EbaDpm.Converter.Core.Validation;

/// <summary>Kind of registered exception.</summary>
public enum ExceptionKind
{
    /// <summary>E-n: defect of the reference database, with positive evidence. CRITICAL layer.</summary>
    Defect,

    /// <summary>DD-n: declared divergence (not derivable, or release drift). Normally the INFORMATIVE layer, unless explicitly declared otherwise (e.g. DD-16, critical).</summary>
    DeclaredDivergence,

    /// <summary>
    /// AO-n: anomaly of the SOURCE, not of the reference database. Plane A, WITHOUT <c>--reference</c>
    /// (<see cref="KnownException.Reference"/> is <c>"-"</c>). Applied with
    /// <see cref="KnownExceptions.ApplyOriginAnomalyExceptions"/>, sibling of
    /// <see cref="KnownExceptions.ApplyContainmentExceptions"/> with no reference file to compare against.
    /// </summary>
    OriginAnomaly,
}

/// <summary>
/// Whether the exception depends on the release cut (boundary-shaped, e.g. <c>AO-3</c>, which exists
/// ONLY at the 4.3 boundary) or not (source-shaped, e.g. <c>DD-16</c>/<c>AO-1</c>, identical at every
/// measured cut). It decides whether the census is one-sided (only "grows" counts) or two-sided
/// (both "grows" AND "shrinks" count). It is an ATTRIBUTE OF THE EXCEPTION, measured, not an ad-hoc
/// decision inside each check.
/// </summary>
public enum Sidedness
{
    /// <summary>
    /// Not classified. An EXPLICIT value, never confused with a real classification. The 27
    /// exceptions that predate the sidedness attribute (E-1..E-6, DD-1..DD-15) carry this value:
    /// classifying them requires measuring each one at two release cuts, it is not assumed by
    /// analogy. The report distinguishes this absence of classification from a real value.
    /// </summary>
    Unclassified,

    /// <summary>Boundary-shaped: depends on the release cut. Only "grows" counts as a failure.</summary>
    OneSided,

    /// <summary>Source-shaped: does not depend on the release cut. Both "grows" AND "shrinks" count as a failure.</summary>
    TwoSided,
}

/// <summary>
/// An entry of the exception registry, promoted to executable code. It is never applied by pattern,
/// category, table or prefix: <see cref="BusinessKey"/> is ALWAYS exact.
/// </summary>
/// <param name="Reference">
/// The reference file it applies to: <c>"EBA_3.2_phase_1.db"</c>, <c>"EBA_4.0_ERRATA_5.db"</c>,
/// <c>"EBA_4.2_Hotfix.db"</c>, <c>"*"</c> if it applies to any, or <c>"-"</c> if it is
/// <see cref="ExceptionKind.OriginAnomaly"/> (plane A, no reference).
/// </param>
/// <param name="ReferenceSha256">
/// The SHA-256 hash of the exact COPY of <see cref="Reference"/> on which this exception was
/// measured: a file with that name may be a different export of the same publication.
/// <c>null</c> if <see cref="Reference"/> is <c>"*"</c> or <c>"-"</c> (no single file to bind it to).
/// It is DATA, not a gate: it does not prevent applying the exception. The report warns ONCE,
/// globally, if the validated copy does not match (hundreds of identical warnings repeated per
/// exception would be noise, not signal).
/// </param>
/// <param name="Check">Id of the check it affects (e.g. <c>"B-DIC-01-DOMAIN-CODE"</c>, <c>"I-XBR-01c"</c>).</param>
public sealed record KnownException(
    string Id,
    ExceptionKind Kind,
    string BusinessKey,
    string Reference,
    string? ReferenceSha256,
    string Check,
    string Reason,
    ValidationLayer Layer,
    Sidedness Sidedness);

/// <summary>
/// Single registry of exceptions: E-1..E-7, DD-1..DD-23, AO-1 and AO-3. It replaces text-duplicated
/// copies in individual tests and in several production files. The report ALWAYS lists these
/// exceptions, applied or not.
/// </summary>
public static class KnownExceptions
{
    private const string Ref32 = "EBA_3.2_phase_1.db";
    private const string Ref40 = "EBA_4.0_ERRATA_5.db";
    private const string Ref42 = "EBA_4.2_Hotfix.db";

    /// <summary>SHA-256 hashes of the three reference copies the exceptions below were measured on.</summary>
    private const string Sha32 = "b9015d00c7401ffc82415698c9106707c32544b05bddbfcafe585e9300feaefe";
    private const string Sha40 = "9e34bf00a23638ec213dac2ec3b724c069f6b1506e341258d2f079b34ed5f90b";
    private const string Sha42 = "12d70122e363f97705dfb30f4d07d5faa2ce63c6714fe3308242c288d4dbebe6";

    /// <summary>The 27 original exceptions (E-1..E-6, DD-1..DD-15). Sidedness is deliberately unclassified: it is not assumed by analogy.</summary>
    private static readonly KnownException[] LegacyExceptions =
    [
        // Implementation note: each exception is bound to the EXACT Check of the sub-census that
        // resolves it, never to "B-DIC-01" as a whole; otherwise a DomainCode exception would leak
        // by accident into the MemberCode census (exceptions are never applied by pattern or by
        // table). In the XBRL-code censuses the BusinessKey uses the LOCAL PART of the code, which is
        // why E-4/E-5 repeat the same text under two different Checks (DIMENSION-CODE and
        // DIMENSION-XBRL compare different columns, even though they coincide in shape after
        // taking the local part).
        new("E-1", ExceptionKind.Defect, "EXC", Ref42, Sha42, "B-DIC-01-DIMENSION-XBRL",
            "orphan dimension from the T7 versioning: no categorisation of the 3.4 instance uses it. " +
            "Reference=4.2 on purpose: under local-part comparison it does not bite against 3.2",
            ValidationLayer.Critical, Sidedness.Unclassified),

        new("E-2", ExceptionKind.Defect, "met_temp", Ref32, Sha32, "B-DIC-01-DOMAIN-CODE",
            "3.2 exporter scaffolding (domain met_temp), absent from the Access", ValidationLayer.Critical, Sidedness.Unclassified),
        new("E-2", ExceptionKind.Defect, "si288", Ref32, Sha32, "B-DIC-01-MEMBER-XBRL",
            "met_temp member duplicating the member itself under MET", ValidationLayer.Critical, Sidedness.Unclassified),
        new("E-2", ExceptionKind.Defect, "si730", Ref32, Sha32, "B-DIC-01-MEMBER-XBRL",
            "met_temp member duplicating the member itself under MET", ValidationLayer.Critical, Sidedness.Unclassified),
        new("E-2", ExceptionKind.Defect, "si731", Ref32, Sha32, "B-DIC-01-MEMBER-XBRL",
            "met_temp member duplicating the member itself under MET", ValidationLayer.Critical, Sidedness.Unclassified),
        new("E-3", ExceptionKind.Defect, "templateDomain", Ref32, Sha32, "B-DIC-01-DOMAIN-CODE",
            "3.2 exporter scaffolding (domain templateDomain), absent from the Access", ValidationLayer.Critical, Sidedness.Unclassified),
        new("E-4", ExceptionKind.Defect, "template", Ref32, Sha32, "B-DIC-01-DIMENSION-CODE",
            "3.2 exporter scaffolding (dimension template), absent from the Access", ValidationLayer.Critical, Sidedness.Unclassified),
        new("E-4", ExceptionKind.Defect, "template", Ref32, Sha32, "B-DIC-01-DIMENSION-XBRL",
            "3.2 exporter scaffolding (dimension template), absent from the Access", ValidationLayer.Critical, Sidedness.Unclassified),
        new("E-5", ExceptionKind.Defect, "filed", Ref32, Sha32, "B-DIC-01-MEMBER-CODE",
            "3.2 exporter scaffolding (member filed), absent from the Access", ValidationLayer.Critical, Sidedness.Unclassified),
        new("E-5", ExceptionKind.Defect, "filed", Ref32, Sha32, "B-DIC-01-METRIC-CODE",
            "3.2 exporter scaffolding (member filed, also a metric), absent from the Access", ValidationLayer.Critical, Sidedness.Unclassified),
        new("E-5", ExceptionKind.Defect, "filed", Ref32, Sha32, "B-DIC-01-MEMBER-XBRL",
            "3.2 exporter scaffolding (member filed), absent from the Access", ValidationLayer.Critical, Sidedness.Unclassified),

        // BusinessKey WITHOUT prefix (the table-group prefix is already stripped in both census
        // queries, whether the original carries "eba_tg" (4.0) or "tg" (3.2)).
        new("E-6", ExceptionKind.Defect, "corep_4.0|IF_Class2_K-Factor_Requirements_-_Additional_Details", Ref40, Sha40, "B-TPL-01-L1-CODE",
            "IF group attributed to COREP in the 4.0 reference (duplicate with a wrongly assigned taxonomy)",
            ValidationLayer.Critical, Sidedness.Unclassified),

        // DD-1..DD-5: XBRLSchemaRef of five modules of the 3.2 reference. Not derivable.
        new("DD-1", ExceptionKind.DeclaredDivergence, "if_3.2|IF_CLASS2", Ref32, Sha32, "B-MOD-02",
            "XBRLSchemaRef with an ITS segment that cannot be derived from the Access", ValidationLayer.Informative, Sidedness.Unclassified),
        new("DD-2", ExceptionKind.DeclaredDivergence, "if_3.2|IF_CLASS3", Ref32, Sha32, "B-MOD-02",
            "XBRLSchemaRef with an ITS segment that cannot be derived from the Access", ValidationLayer.Informative, Sidedness.Unclassified),
        new("DD-3", ExceptionKind.DeclaredDivergence, "if_3.2|IF_GROUPTEST", Ref32, Sha32, "B-MOD-02",
            "XBRLSchemaRef with an ITS segment that cannot be derived from the Access", ValidationLayer.Informative, Sidedness.Unclassified),
        new("DD-4", ExceptionKind.DeclaredDivergence, "if_3.2|IF_TM", Ref32, Sha32, "B-MOD-02",
            "XBRLSchemaRef with an ITS segment that cannot be derived from the Access", ValidationLayer.Informative, Sidedness.Unclassified),
        new("DD-5", ExceptionKind.DeclaredDivergence, "gsii_3.2|GSII", Ref32, Sha32, "B-MOD-02",
            "XBRLSchemaRef with an ITS segment that cannot be derived from the Access", ValidationLayer.Informative, Sidedness.Unclassified),

        // DD-6..DD-15: ten cells with a different IsShaded in the 4.0 reference. Release drift.
        new("DD-6", ExceptionKind.DeclaredDivergence, "{C_07.00.d,0300,0230,0010}", Ref40, Sha40, "B-CEL-02",
            "IsShaded release drift (v4.1 vs 4.0 ERRATA 5)", ValidationLayer.Informative, Sidedness.Unclassified),
        new("DD-7", ExceptionKind.DeclaredDivergence, "{C_07.00.d,0320,0230,0010}", Ref40, Sha40, "B-CEL-02",
            "IsShaded release drift (v4.1 vs 4.0 ERRATA 5)", ValidationLayer.Informative, Sidedness.Unclassified),
        new("DD-8", ExceptionKind.DeclaredDivergence, "{C_09.04,0110,0030,0010}", Ref40, Sha40, "B-CEL-02",
            "IsShaded release drift (v4.1 vs 4.0 ERRATA 5)", ValidationLayer.Informative, Sidedness.Unclassified),
        new("DD-9", ExceptionKind.DeclaredDivergence, "{C_09.04,0120,0030,0010}", Ref40, Sha40, "B-CEL-02",
            "IsShaded release drift (v4.1 vs 4.0 ERRATA 5)", ValidationLayer.Informative, Sidedness.Unclassified),
        new("DD-10", ExceptionKind.DeclaredDivergence, "{C_09.04,0130,0030,0010}", Ref40, Sha40, "B-CEL-02",
            "IsShaded release drift (v4.1 vs 4.0 ERRATA 5)", ValidationLayer.Informative, Sidedness.Unclassified),
        new("DD-11", ExceptionKind.DeclaredDivergence, "{C_09.04,0140,0030,0010}", Ref40, Sha40, "B-CEL-02",
            "IsShaded release drift (v4.1 vs 4.0 ERRATA 5)", ValidationLayer.Informative, Sidedness.Unclassified),
        new("DD-12", ExceptionKind.DeclaredDivergence, "{C_09.04,0150,0020,0010}", Ref40, Sha40, "B-CEL-02",
            "IsShaded release drift (v4.1 vs 4.0 ERRATA 5)", ValidationLayer.Informative, Sidedness.Unclassified),
        new("DD-13", ExceptionKind.DeclaredDivergence, "{C_09.04,0160,0020,0010}", Ref40, Sha40, "B-CEL-02",
            "IsShaded release drift (v4.1 vs 4.0 ERRATA 5)", ValidationLayer.Informative, Sidedness.Unclassified),
        new("DD-14", ExceptionKind.DeclaredDivergence, "{C_02.00.b,0035,0020}", Ref40, Sha40, "B-CEL-02",
            "IsShaded release drift (v4.1 vs 4.0 ERRATA 5)", ValidationLayer.Informative, Sidedness.Unclassified),
        new("DD-15", ExceptionKind.DeclaredDivergence, "{C_02.00.b,0036,0020}", Ref40, Sha40, "B-CEL-02",
            "IsShaded release drift (v4.1 vs 4.0 ERRATA 5)", ValidationLayer.Informative, Sidedness.Unclassified),
    ];

    /// <summary>
    /// AO-1: 50 DPM 1.0 <c>mMember</c> rows whose own <c>MemberCode</c> already contains a ':'. It is
    /// the same phenomenon that makes 49 <c>MemberXBRLCode</c> values duplicated (A-UNQ-03,
    /// informative). Key <c>DomainCode:MemberCode</c>. Two-sided: if a 51st appears (grows) or any
    /// of the 50 stops violating (shrinks, the Access changed), it fails.
    /// </summary>
    private static readonly string[] ColonInMemberCodeKeys =
    [
        "qIT:TI:x19", "qIT:TI:x102", "qIT:TI:x103", "qFA:qAI:qx2005", "qFA:qFI:qx2006",
        "qFT:qFI:qx2006", "qFN:qFI:qx2133", "qFN:qFI:qx2134", "qFN:qFI:qx2135", "qFN:qFI:qx2136",
        "qFN:qFI:qx2139", "qFN:qNF:qx2002", "qFA:qOR:qx2001", "qES:qSR:qx2001", "qES:qSR:qx2012",
        "qES:qSR:qx2023", "qES:qSR:qx2040", "qES:qSR:qx2042", "qES:qSR:qx2043", "qFT:qTA:qx2015",
        "qIT:qTI:qx2006", "qIT:qTI:qx2013", "qIT:qTI:qx2027", "qIT:qTI:qx2033", "qES:qTE:qx2024",
        "qPO:qPL:qx2001", "qPO:qPL:qx2002", "qFN:qFI:qx2148", "qIT:qTI:qx2039", "qAO:qAI:qx2057",
        "qIT:qTI:qx2042", "qFT:qTA:qx2058", "qFT:qTA:qx2059", "qFN:qFI:qx2160", "qFN:qFI:qx2161",
        "qFN:qFI:qx2178", "qFT:qFI:qx2278", "qFT:qFI:qx2279", "qPO:qPL:qx2076", "qAB:qCG:qx2022",
        "qAB:qCG:qx2024", "qAF:qFI:qx2001", "qAF:qFI:qx2018", "qPZ:qPL:qx2058", "qPZ:qPL:qx2059",
        "qAF:qAI:qx2043", "qPZ:qFI:qx2164", "qAF:qFI:qx2285", "qPZ:qPL:qx2081", "qAF:qAI:qx2220",
    ];

    /// <summary>
    /// AO-3: 104 <c>mHierarchyNode</c> nodes whose resolved member does NOT belong to the domain of
    /// their hierarchy. <c>ItemCategory</c> revises the item right at the 4.3 release cut and the
    /// containing subcategory does not follow it; the source asserts both things at once, so it is
    /// not resolved but declared. Key <c>HierarchyCode:MemberDomainCode:MemberCode</c>.
    /// One-sided: there is no candidate under the own domain for these 104 (unlike the 33 of GA30,
    /// which ARE anchored by <c>Dpm20HierarchyNodeLoader</c> and need no exception); only "grows"
    /// counts.
    /// </summary>
    private static readonly string[] MembersOutsideHierarchyDomainKeys =
    [
        "GA4:qIO:q1B", "GA4:qIO:q1C", "GA4:qIO:q1D", "GA4:qIO:q1N", "GA4:qIO:q5C", "GA4:qIO:q5E",
        "GA4:qIO:q5F", "GA4:qIO:q5J", "GA4:qIO:q5K", "GA4:qIO:q5M", "GA4:qIO:q5N", "GA4:qIO:q5O",
        "GA4:qIO:q5P", "GA4:qIO:q5Q", "GA4:qIO:q5R", "GA4:qIO:q5S", "GA4:qIO:q5T", "GA4:qIO:q5U",
        "GA4:qIO:q5V", "GA4:qIO:q5W", "GA4:qIO:q5X", "GA4:qIO:q5Y", "GA4:qIO:q5Z", "GA4:qIO:q6A",
        "GA4:qIO:q6B", "GA4:qIO:q6C", "GA4:qIO:q6D", "GA4:qIO:q6E", "GA4:qIO:q6F", "GA4:qIO:q6G",
        "GA4:qIO:q6I", "GA4:qIO:q6J", "GA4:qIO:q6K", "GA4:qIO:q6L", "GA4:qIO:q6M", "GA4:qIO:q6N",
        "GA4:qIO:q6O", "GA4:qIO:q6P", "GA4:qIO:q6Q", "GA4:qIO:q6R", "GA4:qIO:q6S", "GA4:qIO:q6T",
        "GA4:qIO:q6U", "GA4:qIO:q6Z", "GA4:qIO:q7Y", "GA4:qIO:q7Z", "GA4:qIO:q8A", "GA4:qIO:q9B",
        "GA4:qIO:qx2108",
        "GA4_1:qIO:q1B", "GA4_1:qIO:q1C", "GA4_1:qIO:q1D", "GA4_1:qIO:q1N", "GA4_1:qIO:q5C",
        "GA4_1:qIO:q5E", "GA4_1:qIO:q5F", "GA4_1:qIO:q5J", "GA4_1:qIO:q5K", "GA4_1:qIO:q5M",
        "GA4_1:qIO:q5N", "GA4_1:qIO:q5O", "GA4_1:qIO:q5P", "GA4_1:qIO:q5Q", "GA4_1:qIO:q5R",
        "GA4_1:qIO:q5S", "GA4_1:qIO:q5T", "GA4_1:qIO:q5U", "GA4_1:qIO:q5V", "GA4_1:qIO:q5W",
        "GA4_1:qIO:q5X", "GA4_1:qIO:q5Y", "GA4_1:qIO:q5Z", "GA4_1:qIO:q6A", "GA4_1:qIO:q6B",
        "GA4_1:qIO:q6C", "GA4_1:qIO:q6D", "GA4_1:qIO:q6E", "GA4_1:qIO:q6F", "GA4_1:qIO:q6G",
        "GA4_1:qIO:q6I", "GA4_1:qIO:q6J", "GA4_1:qIO:q6K", "GA4_1:qIO:q6L", "GA4_1:qIO:q6M",
        "GA4_1:qIO:q6N", "GA4_1:qIO:q6O", "GA4_1:qIO:q6P", "GA4_1:qIO:q6Q", "GA4_1:qIO:q6R",
        "GA4_1:qIO:q6S", "GA4_1:qIO:q6T", "GA4_1:qIO:q6U", "GA4_1:qIO:q6Z", "GA4_1:qIO:q7Y",
        "GA4_1:qIO:q7Z", "GA4_1:qIO:q8A", "GA4_1:qIO:q9B", "GA4_1:qIO:qx2108",
        "MC150:qCM:qx2135", "MC150:qCM:qx2136", "MC150:qCM:qx2137", "MC150:qCM:qx2138",
        "MC150:qCM:qx2139", "MC150:qCM:qx2140",
    ];

    /// <summary>
    /// DD-16: 59 hierarchies, by <c>DomainCode:HierarchyCode</c>, that <c>EBA_4.2_Hotfix.db</c>
    /// inherits from DPM 1.0 and that the DPM 2.0 Access does not contain; by design decision these
    /// 59 hierarchies are lost. Two-sided: it does not depend on the release cut (0/59 in
    /// <c>SubCategory</c> in both the 4.2 and the 4.3 Access, measured with a positive control);
    /// source-shaped, a leftover of DPM 1.0 never re-exported.
    /// </summary>
    private static readonly string[] LegacyDpm10HierarchyKeys =
    [
        "AP:AP15", "AP:AP18", "AP:AP24", "AP:AP26_1", "AP:AP30_REL_2",
        "CS:CS4",
        "CU:CU3_1", "CU:CU3_2", "CU:CU3_3", "CU:CU_ALL",
        "GA:GA10", "GA:GA10_1", "GA:GA2", "GA:GA3", "GA:GA5_1", "GA:GA5_2", "GA:GA6_1", "GA:GA9_REL_2",
        "IM:IM50",
        "MC:MC105", "MC:MC140", "MC:MC26", "MC:MC26_1", "MC:MC74", "MC:MC80",
        "MET:AT106", "MET:AT11", "MET:AT6", "MET:AT81", "MET:AT9",
        "NC:NC20", "NC:NC21", "NC:NC30",
        "RP:RP4", "RP:RP7_REL_2",
        "RT:RT1", "RT:RT4",
        "TA:TA32",
        "TI:TI10", "TI:TI11",
        "TR:TR4", "TR:TR7",
        "UE:UE4",
        "ZZ:ZZ13", "ZZ:ZZ14", "ZZ:ZZ18", "ZZ:ZZ23", "ZZ:ZZ27", "ZZ:ZZ3", "ZZ:ZZ33", "ZZ:ZZ50",
        "ZZ:ZZ55", "ZZ:ZZ57", "ZZ:ZZ6", "ZZ:ZZ65", "ZZ:ZZ6_1", "ZZ:ZZ7", "ZZ:ZZ71", "ZZ:ZZ72",
    ];

    private const string ColonInMemberCodeReason =
        "AO-1: the MemberCode itself contains ':' - the same phenomenon that makes 49 MemberXBRLCode values duplicated (A-UNQ-03, informative). Closed census.";

    private const string MembersOutsideHierarchyDomainReason =
        "AO-3: ItemCategory revises the item right at the 4.3 release cut and the containing subcategory does not follow it - the source asserts both things at once, so it is declared, not resolved.";

    private const string LegacyDpm10HierarchyReason =
        "DD-16: DPM 1.0 leftover in the 4.2 reference, not present in the DPM 2.0 Access - by design decision, these 59 hierarchies are lost.";

    /// <summary>
    /// E-7: the 109 <c>mMember</c> rows of domain <c>qIO</c> (by <c>MemberXBRLCode</c>) that the
    /// source has and <c>EBA_4.2_Hotfix.db</c> does not. Unlike <c>DD-16</c>, here we DO EMIT them,
    /// and the entry exists so that nobody "fixes" the converter by deleting these 109 members to
    /// balance a count. Generated-census mode (<c>ApplyGeneratedCensusExceptions</c>) with BOTH
    /// halves: it expires if the reference publishes any of the 109 (it would no longer be a defect
    /// of the reference), AND if we stop emitting any of them (a regression, exactly what the entry
    /// exists to prevent). Two-sided: both directions are real signal, neither can be turned off.
    /// </summary>
    private static readonly string[] UnpublishedQioMemberKeys =
    [
        "eba_qIO:qx2000", "eba_qIO:qx2001", "eba_qIO:qx2002", "eba_qIO:qx2003", "eba_qIO:qx2004", "eba_qIO:qx2005",
        "eba_qIO:qx2006", "eba_qIO:qx2007", "eba_qIO:qx2008", "eba_qIO:qx2009", "eba_qIO:qx2010", "eba_qIO:qx2011",
        "eba_qIO:qx2012", "eba_qIO:qx2013", "eba_qIO:qx2014", "eba_qIO:qx2015", "eba_qIO:qx2016", "eba_qIO:qx2017",
        "eba_qIO:qx2018", "eba_qIO:qx2019", "eba_qIO:qx2020", "eba_qIO:qx2021", "eba_qIO:qx2022", "eba_qIO:qx2023",
        "eba_qIO:qx2024", "eba_qIO:qx2025", "eba_qIO:qx2026", "eba_qIO:qx2027", "eba_qIO:qx2028", "eba_qIO:qx2029",
        "eba_qIO:qx2030", "eba_qIO:qx2031", "eba_qIO:qx2032", "eba_qIO:qx2033", "eba_qIO:qx2034", "eba_qIO:qx2035",
        "eba_qIO:qx2036", "eba_qIO:qx2037", "eba_qIO:qx2038", "eba_qIO:qx2039", "eba_qIO:qx2040", "eba_qIO:qx2041",
        "eba_qIO:qx2042", "eba_qIO:qx2043", "eba_qIO:qx2044", "eba_qIO:qx2045", "eba_qIO:qx2046", "eba_qIO:qx2047",
        "eba_qIO:qx2048", "eba_qIO:qx2049", "eba_qIO:qx2050", "eba_qIO:qx2051", "eba_qIO:qx2052", "eba_qIO:qx2053",
        "eba_qIO:qx2054", "eba_qIO:qx2055", "eba_qIO:qx2056", "eba_qIO:qx2057", "eba_qIO:qx2058", "eba_qIO:qx2059",
        "eba_qIO:qx2060", "eba_qIO:qx2061", "eba_qIO:qx2062", "eba_qIO:qx2063", "eba_qIO:qx2064", "eba_qIO:qx2065",
        "eba_qIO:qx2066", "eba_qIO:qx2067", "eba_qIO:qx2068", "eba_qIO:qx2069", "eba_qIO:qx2070", "eba_qIO:qx2071",
        "eba_qIO:qx2072", "eba_qIO:qx2073", "eba_qIO:qx2074", "eba_qIO:qx2075", "eba_qIO:qx2076", "eba_qIO:qx2077",
        "eba_qIO:qx2078", "eba_qIO:qx2079", "eba_qIO:qx2080", "eba_qIO:qx2081", "eba_qIO:qx2082", "eba_qIO:qx2083",
        "eba_qIO:qx2084", "eba_qIO:qx2085", "eba_qIO:qx2086", "eba_qIO:qx2087", "eba_qIO:qx2088", "eba_qIO:qx2089",
        "eba_qIO:qx2090", "eba_qIO:qx2091", "eba_qIO:qx2092", "eba_qIO:qx2093", "eba_qIO:qx2094", "eba_qIO:qx2095",
        "eba_qIO:qx2096", "eba_qIO:qx2097", "eba_qIO:qx2098", "eba_qIO:qx2099", "eba_qIO:qx2100", "eba_qIO:qx2101",
        "eba_qIO:qx2102", "eba_qIO:qx2103", "eba_qIO:qx2104", "eba_qIO:qx2105", "eba_qIO:qx2106", "eba_qIO:qx2107",
        "eba_qIO:qx0",
    ];

    private const string UnpublishedQioMemberReason =
        "E-7: 109 qIO members that the DPM 2.0 source has and the 4.2 reference leaves empty - unlike DD-16, we DO emit them; the entry exists so that nobody deletes them to balance a count.";

    /// <summary>
    /// DD-17: <c>mRelease.IsCurrent</c>, dynamic by definition. The DPM 2.0 Access marks
    /// <c>IsCurrent=1</c> only on the release of the conversion CUT ('4.2' when converting the 4.2
    /// Access, '4.3' when converting the 4.3 Access), whereas <c>EBA_4.2_Hotfix.db</c> is a fixed
    /// snapshot with all 5 set to 1. Business key = the <c>ReleaseCode</c> that diverges from that
    /// snapshot (4 of the 5: the cut release '4.2' coincides by construction). One-sided: the
    /// diverging VALUE depends on WHICH release is the cut; it changes with every conversion by
    /// design, not by regression. No check consumes it yet (comparing it for equality would be
    /// measuring the clock, not the mapping), so it stays NOT EVALUATED in the report.
    /// </summary>
    private static readonly string[] StaleIsCurrentReleaseKeys = ["4.0", "4.1", "3.5", "3.4"];

    private const string StaleIsCurrentReleaseReason =
        "DD-17: mRelease.IsCurrent is dynamic from one release to the next - not critical, it must not be pinned against any constant.";

    /// <summary>
    /// DD-18: the <c>Order</c> of the orphan <c>BusinessTable</c> rows
    /// (<c>ParentTemplateOrTableID</c>=0) in <c>EBA_4.2_Hotfix.db</c>. Not derivable: three
    /// candidate rules were tried and discarded. There are <b>47</b> of them (verified by direct
    /// query). Business key = <c>TemplateOrTableCode</c>. Unclassified: not measured; the phenomenon
    /// lives in the REFERENCE (an internal counter of its generator), not in the source, so the
    /// release cuts of the Access say nothing about whether it depends on the cut. No check
    /// consumes it yet.
    /// </summary>
    private static readonly string[] OrphanTableOrderKeys =
    [
        "K_18.04", "K_49.02.b", "K_01.00.a", "K_49.02.d", "K_50.00", "K_29.01.b",
        "K_62.01", "K_19.03", "K_00.01", "K_49.02.a", "K_01.00.b", "K_74.00.f",
        "K_00.03", "K_49.01", "K_43.00.a", "K_73.00.b", "K_43.00.b", "K_00.05",
        "K_62.02", "K_49.03.a", "K_44.00", "K_48.00.b", "R_21.00", "R_23.00",
        "R_20.00", "R_22.03", "R_22.01", "Q_10.02", "Q_00.01", "Q_09.01",
        "Q_04.04", "L_08.01", "L_00.02", "F_25.01.d", "F_04.01", "F_08.02",
        "D_08.00.b", "D_03.01", "D_09.02.b", "D_03.02", "D_04.00", "D_09.03.b",
        "D_09.03.a", "C_36.00.a", "C_36.00.b", "I_02.02", "I_02.04",
    ];

    private const string OrphanTableOrderReason =
        "DD-18: Order of the orphan BusinessTable rows - not derivable, the reference numbers them from an internal counter of its generator. 47 rows measured.";

    /// <summary>
    /// DD-20: the <c>Order</c> of the 127 <c>mModuleBusinessTemplate</c> rows. Not derivable (79/127
    /// with the best clean candidate); the natural order of the code is emitted, a declared choice.
    /// Business key = <c>taxonomy|module|TemplateOrTableCode</c>. Bound to <c>B-MOD-01.3</c>, which
    /// already exists and is CRITICAL today; its layer is not changed here. <c>B-MOD-01.3</c> does
    /// not consume <see cref="KnownExceptions"/> yet (it compares with its own logic, not through
    /// <c>ApplyDivergenceExceptions</c>), so these 127 are registered and NOT EVALUATED until it is
    /// decided how they are connected. Unclassified: not measured; the source has no candidate that
    /// derives the Order in any release, so there is no "validity per cut" to check.
    /// </summary>
    private static readonly string[] ModuleTemplateOrderKeys =
    [
        "ae 4.2|AE|eba_tgAE__General_Information", "ae 4.2|AE|eba_tgAsset_Encumbrance",
        "corep 4.2|COREP_ALM|eba_tgAdditional_Liquidity_Monitoring", "corep 4.2|COREP_ALM|eba_tgCoRep__General_Information",
        "corep 4.2|COREP_FRTB|eba_tgCoRep__General_Information", "corep 4.2|COREP_FRTB|eba_tgFRTB_ASA",
        "corep 4.2|COREP_FRTB|eba_tgTHR_BOU_MOV", "corep 4.2|COREP_LCR_DA|eba_tgCoRep__General_Information",
        "corep 4.2|COREP_LCR_DA|eba_tgLiquidity_Coverage__Delegated_Act", "corep 4.2|COREP_LE|eba_tgCoRep__General_Information",
        "corep 4.2|COREP_LE|eba_tgLarge_Exposures", "corep 4.2|COREP_LR|eba_tgCoRep__General_Information",
        "corep 4.2|COREP_LR|eba_tgLeverage_Ratio", "corep 4.2|COREP_NSFR|eba_tgCoRep__General_Information",
        "corep 4.2|COREP_NSFR|eba_tgNSFR__Net_stable_funding_ratio", "corep 4.2|COREP_OF|eba_tgBackstop",
        "corep 4.2|COREP_OF|eba_tgCapital_Adequacy", "corep 4.2|COREP_OF|eba_tgCoRep__General_Information",
        "corep 4.2|COREP_OF|eba_tgCounterparty_Credit_Risk", "corep 4.2|COREP_OF|eba_tgCredit_Risk",
        "corep 4.2|COREP_OF|eba_tgCrypto_assets_Risk", "corep 4.2|COREP_OF|eba_tgGroup_Solvency",
        "corep 4.2|COREP_OF|eba_tgIF_Class2_KFactor_Requirements__Additional_Details", "corep 4.2|COREP_OF|eba_tgOP_LOSS",
        "corep 4.2|COREP_OF|eba_tgOWN_FUNDS", "corep 4.2|COREP_OF|eba_tgPrudent_Valuation",
        "corep 4.2|COREP_OF|eba_tgSovereign_exposures", "esg 4.2|ESG|eba_tgAd_hoc_ESG",
        "fc 4.2|FC|eba_tgFICO_IGT", "fc 4.2|FC|eba_tgFICO_RC",
        "finrep 4.2|FINREP9|eba_tgFinRep__General_Information", "finrep 4.2|FINREP9|eba_tgFinRep__Part_1",
        "finrep 4.2|FINREP9|eba_tgFinRep__Part_1_GAAP_only", "finrep 4.2|FINREP9|eba_tgFinRep__Part_2",
        "finrep 4.2|FINREP9|eba_tgFinRep__Part_3", "finrep 4.2|FINREP9|eba_tgFinRep__Part_4",
        "finrep 4.2|FINREP9DP|eba_tgFinRep__DataPoints", "finrep 4.2|FINREP9DP|eba_tgFinRep__General_Information",
        "finrep 4.2|FINREP9DP|eba_tgFinRep__Part_1", "finrep 4.2|FINREP9DP|eba_tgFinRep__Part_1_GAAP_only",
        "finrep 4.2|FINREP9DP|eba_tgFinRep__Part_2", "finrep 4.2|FINREP9DP|eba_tgFinRep__Part_3",
        "finrep 4.2|FINREP9DP|eba_tgFinRep__Part_4", "fp 4.2|FP|eba_tgFunding_Plans__General_Information",
        "fp 4.2|FP|eba_tgFunding_Plans__Section_1", "fp 4.2|FP|eba_tgFunding_Plans__Section_2",
        "fp 4.2|FP|eba_tgFunding_Plans__Section_4", "fp 4.2|FP|eba_tgFunding_Plans__Section_5",
        "gsii 4.2|GSII|eba_tgGSII", "if 4.2|IF_CLASS2|eba_tgCounterparty_Credit_Risk",
        "if 4.2|IF_CLASS2|eba_tgIF_Class2_Concentration_Risk", "if 4.2|IF_CLASS2|eba_tgIF_Class2_KFactor_Requirements__Additional_Details",
        "if 4.2|IF_CLASS2|eba_tgIF_Class2_Liquidity_Requirements", "if 4.2|IF_CLASS2|eba_tgIF_Class2_Own_Funds",
        "if 4.2|IF_CLASS2|eba_tgIF_Small_and_Noninterconnected_Investment_Firms", "if 4.2|IF_CLASS3|eba_tgIF_Class3_Liquidity_Requirements",
        "if 4.2|IF_CLASS3|eba_tgIF_Class3_Own_Funds", "if 4.2|IF_CLASS3|eba_tgIF_Small_and_Noninterconnected_Investment_Firms",
        "if 4.2|IF_GROUPTEST|eba_tgIF_Group_Capital_Test", "if 4.2|IF_TM|eba_tgIF_Credit_Institution_Threshold_Monitoring",
        "imprac 4.2|NOTIF_IMPRACTICABILITY|eba_tgNotifications_on_impracticability", "ipu 4.2|IPU|eba_tgIntermediate_Parent_Undertaking",
        "irrbb 4.2|IRRBB|eba_tgBreakdown_of_sensitivity_estimates", "irrbb 4.2|IRRBB|eba_tgEvaluation_of_the_IRRBB",
        "irrbb 4.2|IRRBB|eba_tgQualitative_information", "irrbb 4.2|IRRBB|eba_tgRelevant_parameters",
        "irrbb 4.2|IRRBB|eba_tgRepricing_cash_flows", "mica 4.2|MICA|eba_tgGI",
        "mica 4.2|MICA|eba_tgHolders", "mica 4.2|MICA|eba_tgMICA_SA",
        "mica 4.2|MICA|eba_tgReserve_of_assets", "mica 4.2|MICA|eba_tgTokens_and_reserve",
        "mica 4.2|MICA|eba_tgTransactions", "mica 4.2|MICA_OF|eba_tgMICA_OF",
        "mrel 4.2|MREL_DECISIONS|eba_tgMREL_decisions", "mrel 4.2|MREL_TLAC|eba_tgMREL__Creditor_ranking",
        "mrel 4.2|MREL_TLAC|eba_tgMREL__Instrumentbyinstrument_data", "mrel 4.2|MREL_TLAC|eba_tgMREL__capacity_and_composition",
        "pay 4.2|PSD_FRP|eba_tgPSD_Fraudulent_Payment", "pay 4.2|SEPA_IPR|eba_tgIPR",
        "pillar3 4.2|CODIS|eba_tgD_CCyB", "pillar3 4.2|CODIS|eba_tgD_CR_SA",
        "pillar3 4.2|CODIS|eba_tgD_CVA", "pillar3 4.2|CODIS|eba_tgD_Crypto",
        "pillar3 4.2|CODIS|eba_tgD_LR", "pillar3 4.2|CODIS|eba_tgD_MR",
        "pillar3 4.2|CODIS|eba_tgD_OR", "pillar3 4.2|CODIS|eba_tgD_RM_PM",
        "pillar3 4.2|CODIS|eba_tgD_SCOPE", "pillar3 4.2|CODIS|eba_tgD_SEC",
        "pillar3 4.2|CODIS|eba_tgD_SL_E", "pillar3 4.2|CODIS|eba_tgDisclosures_CCR",
        "pillar3 4.2|CODIS|eba_tgDisclosures_CR_IRB", "pillar3 4.2|CODIS|eba_tgDisclosures_OF",
        "pillar3 4.2|CODIS|eba_tgLCR", "pillar3 4.2|CODIS|eba_tgNSFR",
        "pillar3 4.2|ESGDIS|eba_tgDisclosures_ESG", "pillar3 4.2|FINDIS|eba_tgD_CR_Q",
        "pillar3 4.2|FINDIS|eba_tgDisclosures_AE", "pillar3 4.2|GSIIDIS|eba_tgD_GSII",
        "pillar3 4.2|IRRBBDIS|eba_tgD_IRRBB", "pillar3 4.2|MRELTLACDIS|eba_tgD_MREL_TLAC",
        "pillar3 4.2|P3DH|eba_tgP3DH_process", "pillar3 4.2|REMDIS|eba_tgDisclosures_REM",
        "rem 4.2|REM_BM_CI|eba_tgRemuneration_Benchmarking", "rem 4.2|REM_BM_IF|eba_tgRemuneration_Benchmarking__IF",
        "rem 4.2|REM_DBM|eba_tgREM_DB", "rem 4.2|REM_GAP_CI|eba_tgRemuneration_Pament_Gap",
        "rem 4.2|REM_GAP_IF|eba_tgRemuneration_Payment_GAP__IF", "rem 4.2|REM_HE_CI|eba_tgRemuneration_High_earners",
        "rem 4.2|REM_HE_IF|eba_tgRemuneration_High_earners_IF", "rem 4.2|REM_HR_COUNTRY|eba_tgRemuneration_High_Ratios__Country_Level",
        "rem 4.2|REM_HR_INSTITUTION|eba_tgRemuneration_High_Ratios__Institution_Level", "res 4.2|RESOL1|eba_tgRESOL_LIAB",
        "res 4.2|RESOL1|eba_tgRESOL_LIAB_G", "res 4.2|RESOL1|eba_tgRESOL_ORG",
        "res 4.2|RESOL2|eba_tgRESOL_FMI", "res 4.2|RESOL2|eba_tgRESOL_FUNC",
        "res 4.2|RESOL2|eba_tgRESOL_SERV", "sbp 4.2|SBPIMV|eba_tgBenchmarking__General_Information",
        "sbp 4.2|SBPIMV|eba_tgBenchmarking__Initial_Market_Valuation", "sbp 4.2|SBP_CR|eba_tgBenchmarking__Credit_Risk",
        "sbp 4.2|SBP_CR|eba_tgBenchmarking__General_Information", "sbp 4.2|SBP_IFRS9|eba_tgBenchmarking__General_Information",
        "sbp 4.2|SBP_IFRS9|eba_tgBenchmarking__IFRS_9", "sbp 4.2|SBP_RM|eba_tgBenchmarking__General_Information",
        "sbp 4.2|SBP_RM|eba_tgBenchmarking__Market_risk",
    ];

    private const string ModuleTemplateOrderReason =
        "DD-20: Order of mModuleBusinessTemplate - not derivable, the natural order of the code is emitted, a declared choice. Bound to B-MOD-01.3 (critical today).";

    /// <summary>
    /// DD-22: the 12 <c>Role=description</c> translations of <c>mMember</c>: a fossil, the same text
    /// in 3.2, 4.0 and 4.2 (not the 58 of <c>HierarchyNode</c>, which ARE derived, 58 of 58, from
    /// these 12). Business key = <c>DomainCode:MemberCode</c>. Unclassified: not measured;
    /// classifying it requires querying <c>Item.Description</c> of the 4.3 Access for these 12
    /// concepts (via OleDb, not via the generated SQLite, which does not retain that source
    /// column). It is left explicitly unclassified instead of assuming that the stability
    /// 3.2 -> 4.0 -> 4.2 extends to 4.3. No check consumes it yet.
    /// </summary>
    private static readonly string[] MemberDescriptionTranslationKeys =
    [
        "BT:x3", "CP:x34", "CP:x4", "EC:x1", "EC:x16", "MC:x508",
        "OF:x10", "OF:x2", "OF:x6", "OF:x8", "PL:x11", "PL:x51",
    ];

    private const string MemberDescriptionTranslationReason =
        "DD-22: 12 Role=description translations of mMember - a fossil, the same text in 3.2/4.0/4.2; the DPM 2.0 source does not model it in any table.";

    private const string PlaneCTsvResourceName = "EbaDpm.Converter.Core.Resources.plane-c-known-divergences-4.2.tsv";

    private const string PlaneCCensusReason =
        "DD-23: DATED census of the live plane C violations against the EBA Annotated Table Layout " +
        "(src/EbaDpm.Converter.Core/Resources/plane-c-known-divergences-4.2.tsv). Regenerated per release, never relaxed by threshold.";

    /// <summary>
    /// DD-23: the NAMED violations of <c>C-DPS-01</c>, read from
    /// <c>src/EbaDpm.Converter.Core/Resources/plane-c-known-divergences-4.2.tsv</c>, embedded as a
    /// resource (same pattern as the destination schema SQL in <c>SchemaCreator</c>) so it can be
    /// regenerated per release without touching this file. <c>Kind = OriginAnomaly</c> (mechanical,
    /// not a business statement): plane C does not depend on <c>--reference</c>, so, like
    /// <c>AO-1</c>/<c>AO-3</c>, <c>Reference = "-"</c> and it is applied with
    /// <see cref="ApplyOriginAnomalyExceptions"/>, which already reports an entry that stops
    /// violating as EXPIRED. <c>Sidedness = TwoSided</c>: a new violation (grows) and a declared one
    /// that stops violating (shrinks) are both real signal.
    ///
    /// The key is <c>(TableCode, LayoutSignature)</c>, WITHOUT the <c>Type</c> column:
    /// <c>contradicts</c>/<c>no-match</c> is a DIAGNOSIS (classified by
    /// <see cref="PlaneC.PlaneCComparer"/>), not identity; the finding is the same object whichever
    /// way it is labelled. The TSV keeps the second column (the 3-column shape is still read and
    /// validated) and it is included in <see cref="KnownException.Reason"/> row by row, but NOT in
    /// the business key.
    ///
    /// The census carries its SCOPE, <see cref="PlaneCCensusRelease"/> (the
    /// <c>"# CorpusRelease: 4.2"</c> header of the TSV), because it is not portable across
    /// publications: it was measured on DPM 2.0 release 4.2, and applying it to an output of another
    /// release (DPM 1.0, or DPM 2.0 4.3) would trigger the "shrinks" side of
    /// <see cref="ApplyOriginAnomalyExceptions"/> for the wrong reason ("this table was not even
    /// compared", not "the defect was fixed"). The check that decides NOT to evaluate the census
    /// outside its scope is <c>PlaneCChecks</c>; only the scope DATUM lives here, so a future
    /// <c>plane-c-known-divergences-4.3.tsv</c> declares its own without touching a line of C#.
    /// </summary>
    private static readonly Lazy<PlaneCCensus> PlaneCCensusLazy = new(LoadPlaneCDivergences);

    private static IReadOnlyList<KnownException> PlaneCExceptions => PlaneCCensusLazy.Value.Exceptions;

    /// <summary>The DPM 2.0 release on which the plane C divergences TSV was measured
    /// (<c>"# CorpusRelease: ..."</c> header), or <see langword="null"/> if the TSV does not declare
    /// it (defensive: with no declared scope, <c>PlaneCChecks</c> cannot confirm that it applies,
    /// so it does not compare).</summary>
    public static string? PlaneCCensusRelease => PlaneCCensusLazy.Value.CorpusRelease;

    private sealed record PlaneCCensus(IReadOnlyList<KnownException> Exceptions, string? CorpusRelease);

    private static PlaneCCensus LoadPlaneCDivergences()
    {
        var assembly = typeof(KnownExceptions).Assembly;
        using var stream = assembly.GetManifestResourceStream(PlaneCTsvResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{PlaneCTsvResourceName}' not found (DD-23): " +
                "plane-c-known-divergences-4.2.tsv is not registered as an EmbeddedResource in the .csproj.");

        using var textReader = new StreamReader(stream);
        var result = new List<KnownException>();
        string? corpusRelease = null;
        string? line;
        while ((line = textReader.ReadLine()) is not null)
        {
            if (line.StartsWith("# CorpusRelease:", StringComparison.Ordinal))
            {
                // "# CorpusRelease: 4.2 -- free comment" -> "4.2" (first token after ':').
                corpusRelease = line["# CorpusRelease:".Length..].Trim().Split(' ', 2)[0];
                continue;
            }

            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("TableCode\t", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length != 3)
            {
                throw new InvalidOperationException(
                    $"Malformed row in plane-c-known-divergences-4.2.tsv (DD-23): '{line}' - expected 3 " +
                    "tab-separated columns (TableCode, Type, LayoutSignature).");
            }

            // Key WITHOUT the type: PlaneCViolation.BusinessKey uses exactly this same
            // "{TableCode}\t{Signature}" shape.
            var businessKey = $"{parts[0]}\t{parts[2]}";
            var reason = $"{PlaneCCensusReason} Type measured in the census: {parts[1]} (diagnosis, not part of the key).";
            result.Add(new KnownException(
                "DD-23", ExceptionKind.OriginAnomaly, businessKey, "-", null, "C-DPS-01", reason,
                ValidationLayer.Critical, Sidedness.TwoSided));
        }

        return new PlaneCCensus(result, corpusRelease);
    }

    /// <summary>
    /// The complete registry: the 27 original entries + AO-1 (50) + AO-3 (104) + DD-16 (59) + E-7
    /// (109) + DD-17..DD-22 + the plane C census, generated by projection from their compact lists
    /// so that Reason/Check/Layer/Sidedness are not repeated by hand per entry (applying an exception
    /// requires an exact key, not one literal line per entry in the DECLARATION).
    /// </summary>
    public static readonly IReadOnlyList<KnownException> All =
    [
        .. LegacyExceptions,
        .. ColonInMemberCodeKeys.Select(k => new KnownException("AO-1", ExceptionKind.OriginAnomaly, k, "-", null, "I-XBR-01c", ColonInMemberCodeReason, ValidationLayer.Critical, Sidedness.TwoSided)),
        .. MembersOutsideHierarchyDomainKeys.Select(k => new KnownException("AO-3", ExceptionKind.OriginAnomaly, k, "-", null, "I-TRE-07b", MembersOutsideHierarchyDomainReason, ValidationLayer.Critical, Sidedness.OneSided)),
        .. LegacyDpm10HierarchyKeys.Select(k => new KnownException("DD-16", ExceptionKind.DeclaredDivergence, k, Ref42, Sha42, "B-DIC-01.8", LegacyDpm10HierarchyReason, ValidationLayer.Critical, Sidedness.TwoSided)),
        .. UnpublishedQioMemberKeys.Select(k => new KnownException("E-7", ExceptionKind.Defect, k, Ref42, Sha42, "B-DIC-01.9", UnpublishedQioMemberReason, ValidationLayer.Critical, Sidedness.TwoSided)),
        .. StaleIsCurrentReleaseKeys.Select(k => new KnownException("DD-17", ExceptionKind.DeclaredDivergence, k, Ref42, Sha42, "B-DIC-mRelease-IsCurrent", StaleIsCurrentReleaseReason, ValidationLayer.Informative, Sidedness.OneSided)),
        .. OrphanTableOrderKeys.Select(k => new KnownException("DD-18", ExceptionKind.DeclaredDivergence, k, Ref42, Sha42, "B-TPL-Order-Orphans", OrphanTableOrderReason, ValidationLayer.Informative, Sidedness.Unclassified)),
        .. ModuleTemplateOrderKeys.Select(k => new KnownException("DD-20", ExceptionKind.DeclaredDivergence, k, Ref42, Sha42, "B-MOD-01.3", ModuleTemplateOrderReason, ValidationLayer.Informative, Sidedness.Unclassified)),
        .. MemberDescriptionTranslationKeys.Select(k => new KnownException("DD-22", ExceptionKind.DeclaredDivergence, k, Ref42, Sha42, "B-DIC-ConceptTranslation-Description", MemberDescriptionTranslationReason, ValidationLayer.Informative, Sidedness.Unclassified)),
        .. PlaneCExceptions,
    ];

    /// <summary>
    /// The <c>Check</c> ids actually QUERIED this session, to distinguish "NOT EVALUATED" (nobody
    /// called <see cref="For"/> with this <c>Check</c>; the exception check id is deliberately not
    /// the same string as <c>CheckResult.Id</c>, to avoid leaking by block) from "evaluated, no
    /// matches". Reset once per <c>Validator.Run</c> (<see cref="ResetQueryTracking"/>).
    /// </summary>
    private static readonly HashSet<string> QueriedChecks = new(StringComparer.Ordinal);

    /// <summary>
    /// The plane of a <c>checkId</c> is registered HERE, at the same choke point as
    /// <see cref="QueriedChecks"/>; it is never deduced by parsing the identifier prefix. It is
    /// declared by whoever calls <see cref="For"/> (or one of the <c>Apply*</c> methods below, which
    /// call <see cref="For"/> internally): the same place that already knew the plane when building
    /// the adjacent <c>CheckResult</c>.
    /// </summary>
    private static readonly Dictionary<string, ValidationPlane> CheckPlanesById = new(StringComparer.Ordinal);

    public static void ResetQueryTracking()
    {
        QueriedChecks.Clear();
        CheckPlanesById.Clear();
    }

    public static bool WasQueried(string checkId) => QueriedChecks.Contains(checkId);

    /// <summary>
    /// The plane of <paramref name="checkId"/>, as declared by the check that queried it this
    /// session (via <see cref="For"/>). <c>null</c> if no <c>Apply*</c>/<see cref="For"/> has been
    /// called with this id yet, the same case in which <see cref="WasQueried"/> returns <c>false</c>.
    /// </summary>
    public static ValidationPlane? PlaneOf(string checkId) =>
        CheckPlanesById.TryGetValue(checkId, out var plane) ? plane : null;

    public static IEnumerable<KnownException> For(string checkId, ValidationPlane plane)
    {
        QueriedChecks.Add(checkId);
        CheckPlanesById[checkId] = plane;
        return All.Where(e => e.Check == checkId);
    }

    /// <summary>Result of evaluating an exception against the current run.</summary>
    public sealed record Outcome(KnownException Exception, bool AppliesToThisReference, long Matched, bool Stale);

    /// <summary>
    /// Applies the containment exceptions of <paramref name="checkId"/> (by default
    /// <see cref="ExceptionKind.Defect"/>, E-n, but another <paramref name="kind"/> may be passed for
    /// containment-shaped exceptions of a different <c>Kind</c>, e.g. <c>DD-16</c>, which is
    /// <see cref="ExceptionKind.DeclaredDivergence"/> by definition even though its SHAPE is
    /// containment) to a "reference minus generated" set. An exception exempts its exact object
    /// from counting as a failure, but ONLY if the current reference is the declared one and ONLY if
    /// the object is still actually absent when it is applied (if it no longer is, the exception has
    /// expired by "unexpectedMatch", which is EXACTLY the "shrinks" of <c>Sidedness.TwoSided</c>,
    /// applied here unconditionally: for <c>Defect</c>/<c>DeclaredDivergence</c> it was always so).
    /// </summary>
    public static (IReadOnlyList<string> UnresolvedMissing, IReadOnlyList<Outcome> Outcomes) ApplyContainmentExceptions(
        string checkId, string referenceFileName, IReadOnlySet<string> missingFromGenerated, IReadOnlySet<string> referenceSet,
        ValidationPlane plane, ExceptionKind kind = ExceptionKind.Defect)
    {
        var applicable = For(checkId, plane).Where(e => e.Kind == kind).ToList();
        var outcomes = new List<Outcome>();
        var stillMissing = new HashSet<string>(missingFromGenerated, StringComparer.Ordinal);

        foreach (var exception in applicable)
        {
            var appliesToThisReference = exception.Reference == "*"
                || string.Equals(exception.Reference, referenceFileName, StringComparison.OrdinalIgnoreCase);

            if (!appliesToThisReference)
            {
                outcomes.Add(new Outcome(exception, false, 0, false));
                continue;
            }

            if (!referenceSet.Contains(exception.BusinessKey))
            {
                // The exception no longer corresponds to ANY object of the reference -> expired.
                outcomes.Add(new Outcome(exception, true, 0, true));
                continue;
            }

            var wasMissing = missingFromGenerated.Contains(exception.BusinessKey);
            outcomes.Add(new Outcome(exception, true, wasMissing ? 1 : 0, !wasMissing));
            if (wasMissing)
            {
                stillMissing.Remove(exception.BusinessKey);
            }
        }

        return (stillMissing.ToList(), outcomes);
    }

    /// <summary>
    /// Applies the declared-divergence exceptions (Kind = DeclaredDivergence) of
    /// <paramref name="checkId"/> to a dictionary key -> (generated value, reference value).
    /// Returns the UNDECLARED divergences (a real failure; the general criterion is never relaxed)
    /// and the result of each declared exception.
    /// </summary>
    public static (IReadOnlyList<string> UnexpectedDivergences, IReadOnlyList<Outcome> Outcomes) ApplyDivergenceExceptions(
        string checkId, string referenceFileName, IReadOnlyDictionary<string, (string Generated, string Reference)> comparableByKey,
        ValidationPlane plane)
    {
        var applicable = For(checkId, plane).Where(e => e.Kind == ExceptionKind.DeclaredDivergence).ToList();
        var outcomes = new List<Outcome>();
        var declaredAndApplicable = new HashSet<string>(StringComparer.Ordinal);

        foreach (var exception in applicable)
        {
            var appliesToThisReference = exception.Reference == "*"
                || string.Equals(exception.Reference, referenceFileName, StringComparison.OrdinalIgnoreCase);

            if (!appliesToThisReference)
            {
                outcomes.Add(new Outcome(exception, false, 0, false));
                continue;
            }

            declaredAndApplicable.Add(exception.BusinessKey);

            if (!comparableByKey.TryGetValue(exception.BusinessKey, out var pair))
            {
                outcomes.Add(new Outcome(exception, true, 0, true)); // object no longer exists: expired
                continue;
            }

            var diverges = !string.Equals(pair.Generated, pair.Reference, StringComparison.Ordinal);
            outcomes.Add(new Outcome(exception, true, diverges ? 1 : 0, !diverges)); // stopped diverging: expired
        }

        var unexpected = comparableByKey
            .Where(kv => !string.Equals(kv.Value.Generated, kv.Value.Reference, StringComparison.Ordinal))
            .Where(kv => !declaredAndApplicable.Contains(kv.Key))
            .Select(kv => kv.Key)
            .ToList();

        return (unexpected, outcomes);
    }

    /// <summary>
    /// Sibling of <see cref="ApplyContainmentExceptions"/> for <see cref="ExceptionKind.OriginAnomaly"/>:
    /// plane A, WITHOUT a reference file to compare (<c>Reference == "-"</c>, always applies).
    /// <paramref name="violatingKeys"/> is the set of business keys that violate the invariant
    /// TODAY. <see cref="Sidedness"/> decides whether "shrinks" (a declared one stops violating)
    /// counts as a failure: <c>TwoSided</c> (AO-1) yes, <c>OneSided</c> (AO-3) no.
    /// </summary>
    public static (IReadOnlyList<string> UnresolvedViolations, IReadOnlyList<Outcome> Outcomes) ApplyOriginAnomalyExceptions(
        string checkId, IReadOnlySet<string> violatingKeys, ValidationPlane plane)
    {
        var applicable = For(checkId, plane).Where(e => e.Kind == ExceptionKind.OriginAnomaly).ToList();
        var declared = applicable.Select(e => e.BusinessKey).ToHashSet(StringComparer.Ordinal);
        var outcomes = new List<Outcome>();

        var unresolved = violatingKeys.Except(declared, StringComparer.Ordinal).ToList();

        foreach (var exception in applicable)
        {
            var stillViolating = violatingKeys.Contains(exception.BusinessKey);
            if (exception.Sidedness == Sidedness.TwoSided && !stillViolating)
            {
                // "Shrinks": the declared exception no longer violates, a sign that the source
                // changed; it is reported.
                outcomes.Add(new Outcome(exception, true, 0, true));
                unresolved.Add($"(absent, {exception.Id} no longer violates) {exception.BusinessKey}");
            }
            else
            {
                outcomes.Add(new Outcome(exception, true, stillViolating ? 1 : 0, false));
            }
        }

        return (unresolved, outcomes);
    }

    /// <summary>
    /// Generated-census mode for <c>E-7</c>: neither containment nor divergence. It verifies TWO
    /// halves over the <see cref="ExceptionKind.Defect"/> entries of <paramref name="checkId"/>:
    /// that the GENERATED output still contains each declared key (if it does not, it is a
    /// REGRESSION, exactly what the entry exists to prevent), and that the REFERENCE still lacks it
    /// (if it starts publishing it, the entry became OBSOLETE: the reference defect was fixed). Both
    /// count as signal; <see cref="Sidedness.TwoSided"/> is the only meaningful shape here, not a
    /// choice: an <c>E-*</c> without the regression half protects nothing.
    /// </summary>
    public static (IReadOnlyList<string> Failing, IReadOnlyList<Outcome> Outcomes) ApplyGeneratedCensusExceptions(
        string checkId, string referenceFileName, IReadOnlySet<string> generatedSet, IReadOnlySet<string> referenceSet,
        ValidationPlane plane)
    {
        var applicable = For(checkId, plane).Where(e => e.Kind == ExceptionKind.Defect).ToList();
        var outcomes = new List<Outcome>();
        var failing = new List<string>();

        foreach (var exception in applicable)
        {
            var appliesToThisReference = exception.Reference == "*"
                || string.Equals(exception.Reference, referenceFileName, StringComparison.OrdinalIgnoreCase);

            if (!appliesToThisReference)
            {
                outcomes.Add(new Outcome(exception, false, 0, false));
                continue;
            }

            var inGenerated = generatedSet.Contains(exception.BusinessKey);
            var inReference = referenceSet.Contains(exception.BusinessKey);

            if (!inGenerated)
            {
                // Regression: we stopped emitting it. This is EXACTLY what the entry exists to prevent.
                outcomes.Add(new Outcome(exception, true, 0, true));
                failing.Add($"(E-7 regression: we NO LONGER emit it) {exception.BusinessKey}");
                continue;
            }

            if (inReference)
            {
                // The reference already publishes it: the entry became obsolete, not a failure of ours.
                outcomes.Add(new Outcome(exception, true, 1, true));
                failing.Add($"(E-7 obsolete: the reference ALREADY publishes it) {exception.BusinessKey}");
                continue;
            }

            outcomes.Add(new Outcome(exception, true, 1, false));
        }

        return (failing, outcomes);
    }
}
