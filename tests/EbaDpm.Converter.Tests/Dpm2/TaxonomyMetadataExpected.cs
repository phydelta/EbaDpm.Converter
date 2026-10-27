namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Expected DPM 2.0 <c>mTaxonomy</c> metadata (issue #11) for every emitted taxonomy of the three
/// real outputs, keyed by <c>TaxonomyCode</c>. Since issue 66 the taxonomies are the distinct
/// (framework, <c>ModuleVersion.StartReleaseID</c>) of the emitted module versions (18, 19 and 21
/// rows); the set of codes is checked independently against the Access database by
/// <c>Dpm20TaxonomyPerStartReleaseDataTests</c>, and the metadata values of the rows that remain
/// are the ones derived for issue #11. Each line is
/// <c>TaxonomyCode|TaxonomyLabel|Version|PublicationDate|FromDate|ToDate</c>.
///
/// DERIVED FROM THE ACCESS DATABASES, not from converter output: <c>[Release]</c>, <c>[Framework]</c>
/// and <c>[Module]</c> joined to <c>ModuleVersion</c> were read through ACE OLEDB and the rule of
/// <c>docs/mapping-dpm2.md</c> (R', label version, FromDate, ToDate chain) was applied by an
/// independent script (the rule as written in issue #11, with no code shared with the converter).
/// The set of codes is the emitted set of the converter (<c>--list-taxonomies</c>); only the
/// metadata columns are asserted against these values. The values were cross-checked against the
/// anchors measured by hand on the 4.3 database (corep 4.3, corep 4.2, corep 4.0, esg 4.1).
/// </summary>
internal static class TaxonomyMetadataExpected
{
    /// <summary>"DPM2 Database_v 4_3_20260622.accdb", natural cutoff 4.3: 21 taxonomies.</summary>
    public const string Release43 = """
            ae 4.2|Asset Encumbrance 1.4.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            amla 4.3|EBA-AMLA collaboration 1.0.0 (DPM 4.3)|4.3|2026-06-28|2026-12-31|9999-12-31
            corep 4.2|Common Reporting 4.1.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            esg 4.2|ESG 1.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            fc 4.2|FICO 1.1.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            finrep 4.2|Financial Reporting 3.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            finrep 4.2.1|Financial Reporting 1.1.0 (DPM 4.2.1)|4.2.1|2026-02-15|2026-03-31|9999-12-31
            fp 4.2|Funding Plans 2.2.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            gsii 4.2|Global systemic and important Institutions 1.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            if 4.2|Investment Firms 1.4.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            imprac 4.2|Impracticability of contractual recognition of bail-in 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            ipu 4.2|Intermediate Parent Undertaking 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            irrbb 4.2|Interest Rate Risk in the Banking Book 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            mica 4.2|MICA 2.0.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            mrel 4.2|MREL and TLAC 1.4.0 (DPM 4.2)|4.2|2025-10-31|2025-12-31|9999-12-31
            pay 4.2|Payments 1.2.0 (DPM 4.2)|4.2|2025-10-31|2022-12-31|9999-12-31
            pillar3 4.2|Pillar 3 disclosures 2.1.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            rem 4.2|Remuneration 2.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            res 4.2|Resolution 2.0.0 (DPM 4.2)|4.2|2025-10-31|2025-12-31|9999-12-31
            sbp 4.2|Supervisory Benchmarking Portfolios 1.5.0 (DPM 4.2)|4.2|2025-10-31|2026-02-27|9999-12-31
            tcb 4.3|Third-country Branches 1.0.0 (DPM 4.3)|4.3|2026-06-28|2027-03-31|9999-12-31
""";

    /// <summary>"DPM2 Database_v 4_2_1.accdb", natural cutoff 4.2.1: 19 taxonomies.</summary>
    public const string Release421Natural = """
            ae 4.2|Asset Encumbrance 1.4.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            corep 4.2|Common Reporting 4.1.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            esg 4.2|ESG 1.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            fc 4.2|FICO 1.1.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            finrep 4.2|Financial Reporting 3.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            finrep 4.2.1|Financial Reporting 1.1.0 (DPM 4.2.1)|4.2.1|2026-02-15|2026-03-31|9999-12-31
            fp 4.2|Funding Plans 2.2.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            gsii 4.2|Global systemic and important Institutions 1.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            if 4.2|Investment Firms 1.4.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            imprac 4.2|Impracticability of contractual recognition of bail-in 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            ipu 4.2|Intermediate Parent Undertaking 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            irrbb 4.2|Interest Rate Risk in the Banking Book 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            mica 4.2|MICA 2.0.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            mrel 4.2|MREL and TLAC 1.4.0 (DPM 4.2)|4.2|2025-10-31|2025-12-31|9999-12-31
            pay 4.2|Payments 1.2.0 (DPM 4.2)|4.2|2025-10-31|2022-12-31|9999-12-31
            pillar3 4.2|Pillar 3 disclosures 2.1.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            rem 4.2|Remuneration 2.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            res 4.2|Resolution 2.0.0 (DPM 4.2)|4.2|2025-10-31|2025-12-31|9999-12-31
            sbp 4.2|Supervisory Benchmarking Portfolios 1.5.0 (DPM 4.2)|4.2|2025-10-31|2026-02-27|9999-12-31
""";

    /// <summary>"DPM2 Database_v 4_2_1.accdb" with the explicit cutoff 4.2: 18 taxonomies.</summary>
    public const string Release42Cutoff = """
            ae 4.2|Asset Encumbrance 1.4.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            corep 4.2|Common Reporting 4.1.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            esg 4.2|ESG 1.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            fc 4.2|FICO 1.1.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            finrep 4.2|Financial Reporting 3.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            fp 4.2|Funding Plans 2.2.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            gsii 4.2|Global systemic and important Institutions 1.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            if 4.2|Investment Firms 1.4.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            imprac 4.2|Impracticability of contractual recognition of bail-in 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            ipu 4.2|Intermediate Parent Undertaking 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            irrbb 4.2|Interest Rate Risk in the Banking Book 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            mica 4.2|MICA 2.0.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            mrel 4.2|MREL and TLAC 1.4.0 (DPM 4.2)|4.2|2025-10-31|2025-12-31|9999-12-31
            pay 4.2|Payments 1.2.0 (DPM 4.2)|4.2|2025-10-31|2022-12-31|9999-12-31
            pillar3 4.2|Pillar 3 disclosures 2.1.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            rem 4.2|Remuneration 2.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            res 4.2|Resolution 2.0.0 (DPM 4.2)|4.2|2025-10-31|2025-12-31|9999-12-31
            sbp 4.2|Supervisory Benchmarking Portfolios 1.5.0 (DPM 4.2)|4.2|2025-10-31|2026-02-27|9999-12-31
""";

    public sealed record Row(string TaxonomyCode, string TaxonomyLabel, string Version, string PublicationDate, string FromDate, string ToDate);

    public static IReadOnlyDictionary<string, Row> Parse(string table)
    {
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var line in table.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split('|');
            if (p.Length != 6)
            {
                throw new InvalidOperationException($"Malformed expected line: '{line}'.");
            }

            rows.Add(p[0], new Row(p[0], p[1], p[2], p[3], p[4], p[5]));
        }

        return rows;
    }
}
