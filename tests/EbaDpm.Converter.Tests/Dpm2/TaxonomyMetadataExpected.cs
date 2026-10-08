namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Expected DPM 2.0 <c>mTaxonomy</c> metadata (issue #11) for every emitted taxonomy of the three
/// real outputs, keyed by <c>TaxonomyCode</c>. Each line is
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
    /// <summary>"DPM2 Database_v 4_3_20260622.accdb", natural cutoff 4.3: 53 taxonomies.</summary>
    public const string Release43 = """
            ae 4.2|Asset Encumbrance 1.4.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            ae 4.3|Asset Encumbrance 1.4.0 (DPM 4.3)|4.3|2026-06-28|2026-03-31|9999-12-31
            amla 4.3|EBA-AMLA collaboration 1.0.0 (DPM 4.3)|4.3|2026-06-28|2026-12-31|9999-12-31
            corep 4.0|Common Reporting 4.0.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2026-03-30
            corep 4.2|Common Reporting 4.1.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            corep 4.3|Common Reporting 4.1.0 (DPM 4.3)|4.3|2026-06-28|2026-03-31|9999-12-31
            esg 4.0|ESG 1.1.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2025-06-29
            esg 4.1|ESG 1.2.0 (DPM 4.1)|4.1|2025-04-28|2025-06-30|2026-03-30
            esg 4.2|ESG 1.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            esg 4.3|ESG 1.3.0 (DPM 4.3)|4.3|2026-06-28|2026-03-31|9999-12-31
            fc 3.5|FICO 1.0.0 (DPM 3.5)|3.5|2024-07-11|2024-12-31|2026-03-30
            fc 4.0|FICO 1.1.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2026-03-30
            fc 4.2|FICO 1.1.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            fc 4.3|FICO 1.1.1 (DPM 4.3)|4.3|2026-06-28|2026-03-31|9999-12-31
            finrep 4.0|Financial Reporting 3.2.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2026-03-30
            finrep 4.2|Financial Reporting 3.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            finrep 4.2.1|Financial Reporting 1.1.0 (DPM 4.2.1)|4.2.1|2026-02-15|2026-03-31|9999-12-31
            finrep 4.3|Financial Reporting 1.1.0 (DPM 4.3)|4.3|2026-06-28|2026-03-31|9999-12-31
            fp 4.2|Funding Plans 2.2.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            fp 4.3|Funding Plans 2.2.1 (DPM 4.3)|4.3|2026-06-28|2026-03-31|9999-12-31
            gsii 4.2|Global systemic and important Institutions 1.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            gsii 4.3|Global systemic and important Institutions 1.3.0 (DPM 4.3)|4.3|2026-06-28|2026-03-31|9999-12-31
            if 4.0|Investment Firms 1.3.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2026-03-30
            if 4.2|Investment Firms 1.4.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            if 4.3|Investment Firms 1.4.0 (DPM 4.3)|4.3|2026-06-28|2026-03-31|9999-12-31
            imprac 4.2|Impracticability of contractual recognition of bail-in 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            imprac 4.3|Impracticability of contractual recognition of bail-in 1.2.0 (DPM 4.3)|4.3|2026-06-28|2026-03-31|9999-12-31
            ipu 4.2|Intermediate Parent Undertaking 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            ipu 4.3|Intermediate Parent Undertaking 1.2.0 (DPM 4.3)|4.3|2026-06-28|2026-03-31|9999-12-31
            irrbb 4.2|Interest Rate Risk in the Banking Book 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            irrbb 4.3|Interest Rate Risk in the Banking Book 1.2.0 (DPM 4.3)|4.3|2026-06-28|2026-03-31|9999-12-31
            mica 4.0|MICA 1.0.0 (DPM 4.0)|4.0|2024-12-19|2025-03-31|2025-06-29
            mica 4.1|MICA 2.0.0 (DPM 4.1)|4.1|2025-04-28|2025-06-30|2026-03-30
            mica 4.2|MICA 2.0.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            mica 4.3|MICA 2.0.1 (DPM 4.3)|4.3|2026-06-28|2026-03-31|9999-12-31
            mrel 4.2|MREL and TLAC 1.4.0 (DPM 4.2)|4.2|2025-10-31|2025-12-31|9999-12-31
            mrel 4.3|MREL and TLAC 1.4.0 (DPM 4.3)|4.3|2026-06-28|2025-12-31|9999-12-31
            pay 4.1|Payments 1.0.0 (DPM 4.1)|4.1|2025-04-28|2022-12-31|9999-12-31
            pay 4.2|Payments 1.2.0 (DPM 4.2)|4.2|2025-10-31|2022-12-31|9999-12-31
            pay 4.3|Payments 1.2.0 (DPM 4.3)|4.3|2026-06-28|2022-12-31|9999-12-31
            pillar3 4.0|Pillar 3 disclosures 1.1.0 (DPM 4.0)|4.0|2024-12-19|2023-12-31|2025-06-29
            pillar3 4.1|Pillar 3 disclosures 2.0.0 (DPM 4.1)|4.1|2025-04-28|2025-06-30|2026-03-30
            pillar3 4.2|Pillar 3 disclosures 2.1.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            pillar3 4.3|Pillar 3 disclosures 2.1.0 (DPM 4.3)|4.3|2026-06-28|2026-03-31|9999-12-31
            rem 3.5|Remuneration 2.1.0 (DPM 3.5)|3.5|2024-07-11|2024-12-31|2026-03-30
            rem 4.0|Remuneration 2.2.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2026-03-30
            rem 4.2|Remuneration 2.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            rem 4.3|Remuneration 2.3.0 (DPM 4.3)|4.3|2026-06-28|2026-03-31|9999-12-31
            res 4.2|Resolution 2.0.0 (DPM 4.2)|4.2|2025-10-31|2025-12-31|9999-12-31
            res 4.3|Resolution 2.0.0 (DPM 4.3)|4.3|2026-06-28|2025-12-31|9999-12-31
            sbp 4.2|Supervisory Benchmarking Portfolios 1.5.0 (DPM 4.2)|4.2|2025-10-31|2026-02-27|9999-12-31
            sbp 4.3|Supervisory Benchmarking Portfolios 1.5.0 (DPM 4.3)|4.3|2026-06-28|2026-02-27|9999-12-31
            tcb 4.3|Third-country Branches 1.0.0 (DPM 4.3)|4.3|2026-06-28|2027-03-31|9999-12-31
""";

    /// <summary>"DPM2 Database_v 4_2_1.accdb", natural cutoff 4.2.1: 50 taxonomies.</summary>
    public const string Release421Natural = """
            ae 4.2|Asset Encumbrance 1.4.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            ae 4.2.1|Asset Encumbrance 1.4.0 (DPM 4.2.1)|4.2.1|2026-02-15|2026-03-31|9999-12-31
            corep 4.0|Common Reporting 4.0.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2026-03-30
            corep 4.2|Common Reporting 4.1.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            corep 4.2.1|Common Reporting 4.1.0 (DPM 4.2.1)|4.2.1|2026-02-15|2026-03-31|9999-12-31
            esg 4.0|ESG 1.1.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2025-06-29
            esg 4.1|ESG 1.2.0 (DPM 4.1)|4.1|2025-04-28|2025-06-30|2026-03-30
            esg 4.2|ESG 1.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            esg 4.2.1|ESG 1.3.0 (DPM 4.2.1)|4.2.1|2026-02-15|2026-03-31|9999-12-31
            fc 3.5|FICO 1.0.0 (DPM 3.5)|3.5|2024-07-11|2024-12-31|2026-03-30
            fc 4.0|FICO 1.1.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2026-03-30
            fc 4.2|FICO 1.1.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            fc 4.2.1|FICO 1.1.1 (DPM 4.2.1)|4.2.1|2026-02-15|2026-03-31|9999-12-31
            finrep 4.0|Financial Reporting 3.2.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2026-03-30
            finrep 4.2|Financial Reporting 3.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            finrep 4.2.1|Financial Reporting 1.1.0 (DPM 4.2.1)|4.2.1|2026-02-15|2026-03-31|9999-12-31
            fp 4.2|Funding Plans 2.2.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            fp 4.2.1|Funding Plans 2.2.1 (DPM 4.2.1)|4.2.1|2026-02-15|2026-03-31|9999-12-31
            gsii 4.2|Global systemic and important Institutions 1.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            gsii 4.2.1|Global systemic and important Institutions 1.3.0 (DPM 4.2.1)|4.2.1|2026-02-15|2026-03-31|9999-12-31
            if 4.0|Investment Firms 1.3.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2026-03-30
            if 4.2|Investment Firms 1.4.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            if 4.2.1|Investment Firms 1.4.0 (DPM 4.2.1)|4.2.1|2026-02-15|2026-03-31|9999-12-31
            imprac 4.2|Impracticability of contractual recognition of bail-in 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            imprac 4.2.1|Impracticability of contractual recognition of bail-in 1.2.0 (DPM 4.2.1)|4.2.1|2026-02-15|2026-03-31|9999-12-31
            ipu 4.2|Intermediate Parent Undertaking 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            ipu 4.2.1|Intermediate Parent Undertaking 1.2.0 (DPM 4.2.1)|4.2.1|2026-02-15|2026-03-31|9999-12-31
            irrbb 4.2|Interest Rate Risk in the Banking Book 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            irrbb 4.2.1|Interest Rate Risk in the Banking Book 1.2.0 (DPM 4.2.1)|4.2.1|2026-02-15|2026-03-31|9999-12-31
            mica 4.0|MICA 1.0.0 (DPM 4.0)|4.0|2024-12-19|2025-03-31|2025-06-29
            mica 4.1|MICA 2.0.0 (DPM 4.1)|4.1|2025-04-28|2025-06-30|2026-03-30
            mica 4.2|MICA 2.0.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            mica 4.2.1|MICA 2.0.1 (DPM 4.2.1)|4.2.1|2026-02-15|2026-03-31|9999-12-31
            mrel 4.2|MREL and TLAC 1.4.0 (DPM 4.2)|4.2|2025-10-31|2025-12-31|9999-12-31
            mrel 4.2.1|MREL and TLAC 1.4.0 (DPM 4.2.1)|4.2.1|2026-02-15|2025-12-31|9999-12-31
            pay 4.1|Payments 1.0.0 (DPM 4.1)|4.1|2025-04-28|2022-12-31|9999-12-31
            pay 4.2|Payments 1.2.0 (DPM 4.2)|4.2|2025-10-31|2022-12-31|9999-12-31
            pay 4.2.1|Payments 1.2.0 (DPM 4.2.1)|4.2.1|2026-02-15|2022-12-31|9999-12-31
            pillar3 4.0|Pillar 3 disclosures 1.1.0 (DPM 4.0)|4.0|2024-12-19|2023-12-31|2025-06-29
            pillar3 4.1|Pillar 3 disclosures 2.0.0 (DPM 4.1)|4.1|2025-04-28|2025-06-30|2026-03-30
            pillar3 4.2|Pillar 3 disclosures 2.1.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            pillar3 4.2.1|Pillar 3 disclosures 2.1.0 (DPM 4.2.1)|4.2.1|2026-02-15|2026-03-31|9999-12-31
            rem 3.5|Remuneration 2.1.0 (DPM 3.5)|3.5|2024-07-11|2024-12-31|2026-03-30
            rem 4.0|Remuneration 2.2.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2026-03-30
            rem 4.2|Remuneration 2.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            rem 4.2.1|Remuneration 2.3.0 (DPM 4.2.1)|4.2.1|2026-02-15|2026-03-31|9999-12-31
            res 4.2|Resolution 2.0.0 (DPM 4.2)|4.2|2025-10-31|2025-12-31|9999-12-31
            res 4.2.1|Resolution 2.0.0 (DPM 4.2.1)|4.2.1|2026-02-15|2025-12-31|9999-12-31
            sbp 4.2|Supervisory Benchmarking Portfolios 1.5.0 (DPM 4.2)|4.2|2025-10-31|2026-02-27|9999-12-31
            sbp 4.2.1|Supervisory Benchmarking Portfolios 1.5.0 (DPM 4.2.1)|4.2.1|2026-02-15|2026-02-27|9999-12-31
""";

    /// <summary>"DPM2 Database_v 4_2_1.accdb" with the explicit cutoff 4.2: 32 taxonomies.</summary>
    public const string Release42Cutoff = """
            ae 4.2|Asset Encumbrance 1.4.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            corep 4.0|Common Reporting 4.0.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2026-03-30
            corep 4.2|Common Reporting 4.1.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            esg 4.0|ESG 1.1.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2025-06-29
            esg 4.1|ESG 1.2.0 (DPM 4.1)|4.1|2025-04-28|2025-06-30|2026-03-30
            esg 4.2|ESG 1.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            fc 3.5|FICO 1.0.0 (DPM 3.5)|3.5|2024-07-11|2024-12-31|2026-03-30
            fc 4.0|FICO 1.1.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2026-03-30
            fc 4.2|FICO 1.1.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            finrep 4.0|Financial Reporting 3.2.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2026-03-30
            finrep 4.2|Financial Reporting 3.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            fp 4.2|Funding Plans 2.2.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            gsii 4.2|Global systemic and important Institutions 1.3.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            if 4.0|Investment Firms 1.3.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2026-03-30
            if 4.2|Investment Firms 1.4.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            imprac 4.2|Impracticability of contractual recognition of bail-in 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            ipu 4.2|Intermediate Parent Undertaking 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            irrbb 4.2|Interest Rate Risk in the Banking Book 1.2.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            mica 4.0|MICA 1.0.0 (DPM 4.0)|4.0|2024-12-19|2025-03-31|2025-06-29
            mica 4.1|MICA 2.0.0 (DPM 4.1)|4.1|2025-04-28|2025-06-30|2026-03-30
            mica 4.2|MICA 2.0.1 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            mrel 4.2|MREL and TLAC 1.4.0 (DPM 4.2)|4.2|2025-10-31|2025-12-31|9999-12-31
            pay 4.1|Payments 1.0.0 (DPM 4.1)|4.1|2025-04-28|2022-12-31|9999-12-31
            pay 4.2|Payments 1.2.0 (DPM 4.2)|4.2|2025-10-31|2022-12-31|9999-12-31
            pillar3 4.0|Pillar 3 disclosures 1.1.0 (DPM 4.0)|4.0|2024-12-19|2023-12-31|2025-06-29
            pillar3 4.1|Pillar 3 disclosures 2.0.0 (DPM 4.1)|4.1|2025-04-28|2025-06-30|2026-03-30
            pillar3 4.2|Pillar 3 disclosures 2.1.0 (DPM 4.2)|4.2|2025-10-31|2026-03-31|9999-12-31
            rem 3.5|Remuneration 2.1.0 (DPM 3.5)|3.5|2024-07-11|2024-12-31|2026-03-30
            rem 4.0|Remuneration 2.2.0 (DPM 4.0)|4.0|2024-12-19|2024-12-31|2026-03-30
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
