using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.Checks;

/// <summary>
/// Modules (<c>mModule</c>, <c>mConceptualModule</c>, <c>mModuleBusinessTemplate</c>).
/// Plane A, without looking at any reference.
///
/// A-MOD-09 (not every taxonomy has modules) is an ANTI-invariant: it is not checked here.
/// </summary>
public static class ModuleChecks
{
    private static readonly Regex XbrlSchemaRefPattern = new(
        @"^http://www\.eba\.europa\.eu/eu/fr/xbrl/crr/fws/(?<key>[^/]+/[^/]+(/[0-9]{4}-[0-9]{2}-[0-9]{2})?)/mod/(?<code>[^/]+)\.xsd$",
        RegexOptions.Compiled);

    private static readonly Regex FrameworkVersionUrlPattern = new(
        @"/fws/[^/]+/[^/]+_[0-9]", RegexOptions.Compiled);

    public static IEnumerable<CheckResult> Run(SqliteConnection c)
    {
        yield return ConceptualModuleMirrorsModule(c);

        yield return SqlHelpers.CountCheck(
            c, "A-MOD-02.1", ValidationLayer.Critical, "mModule", "DefaultFrequency NULL in 100% of rows",
            "SELECT COUNT(*) FROM \"mModule\"", "SELECT COUNT(*) FROM \"mModule\" WHERE \"DefaultFrequency\" IS NOT NULL");
        yield return SqlHelpers.CountCheck(
            c, "A-MOD-02.2", ValidationLayer.Critical, "mModule", "JSONSchemaRef NULL in 100% of rows",
            "SELECT COUNT(*) FROM \"mModule\"", "SELECT COUNT(*) FROM \"mModule\" WHERE \"JSONSchemaRef\" IS NOT NULL");
        yield return SqlHelpers.CountCheck(
            c, "A-MOD-02.3", ValidationLayer.Critical, "mModule", "AutogenerateRefs = 1 in 100% of rows",
            "SELECT COUNT(*) FROM \"mModule\"", "SELECT COUNT(*) FROM \"mModule\" WHERE \"AutogenerateRefs\" IS NOT 1");
        yield return SqlHelpers.CountCheck(
            c, "A-MOD-02.4", ValidationLayer.Critical, "mModule", "JsonBlob is a zero-length BLOB, not NULL",
            "SELECT COUNT(*) FROM \"mModule\"", "SELECT COUNT(*) FROM \"mModule\" WHERE \"JsonBlob\" IS NULL OR length(\"JsonBlob\") <> 0");

        yield return XbrlSchemaRefNeverNullAndWellFormed(c);
        yield return NoFrameworkVersionUrl(c);

        yield return SqlHelpers.CountCheck(
            c, "A-MOD-05", ValidationLayer.Critical, "mModuleBusinessTemplate", "Every BusinessTemplateID points to a TableGroup/Level=1 node",
            "SELECT COUNT(*) FROM \"mModuleBusinessTemplate\"",
            """
            SELECT COUNT(*) FROM "mModuleBusinessTemplate" mbt
            JOIN "mTemplateOrTable" tot ON tot."TemplateOrTableID" = mbt."BusinessTemplateID"
            WHERE tot."TemplateOrTableType" <> 'TableGroup' OR tot."Level" <> 1
            """);

        yield return SqlHelpers.CountCheck(
            c, "A-MOD-06", ValidationLayer.Critical, "mModuleBusinessTemplate", "The referenced node belongs to the same taxonomy as the module",
            "SELECT COUNT(*) FROM \"mModuleBusinessTemplate\"",
            """
            SELECT COUNT(*) FROM "mModuleBusinessTemplate" mbt
            JOIN "mModule" m ON m."ModuleID" = mbt."ModuleID"
            JOIN "mTemplateOrTable" tot ON tot."TemplateOrTableID" = mbt."BusinessTemplateID"
            WHERE tot."TaxonomyID" <> m."TaxonomyID"
            """);

        yield return ModuleBusinessTemplateOrderIsDenseZeroBased(c);

        yield return TaxonomyKeyBijection(c);
    }

    private static CheckResult ConceptualModuleMirrorsModule(SqliteConnection c)
    {
        var totalSql = "SELECT COUNT(*) FROM \"mModule\"";
        var violationSql =
            """
            SELECT m."ModuleID" FROM "mModule" m
            LEFT JOIN "mConceptualModule" cm ON cm."ConceptualModuleID" = m."ModuleID"
            WHERE cm."ConceptualModuleID" IS NULL
               OR cm."ConceptualModuleCode" IS NOT m."ModuleCode"
               OR cm."ConceptualModuleLabel" IS NOT m."ModuleLabel"
            """;
        return SqlHelpers.ViolationCheck(
            c, "A-MOD-01", ValidationLayer.Critical, "mConceptualModule",
            "|mConceptualModule| == |mModule|, ConceptualModuleID/code/label 1:1", totalSql, violationSql);
    }

    private static CheckResult XbrlSchemaRefNeverNullAndWellFormed(SqliteConnection c)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var nullCount = SqlHelpers.Scalar(c, "SELECT COUNT(*) FROM \"mModule\" WHERE \"XBRLSchemaRef\" IS NULL");
        var rows = SqlHelpers.Rows(c, "SELECT \"ModuleCode\", \"XBRLSchemaRef\" FROM \"mModule\" WHERE \"XBRLSchemaRef\" IS NOT NULL", 2);

        var malformed = new List<CheckSample>();
        foreach (var row in rows)
        {
            var moduleCode = row[0]!;
            var schemaRef = row[1]!;
            var match = XbrlSchemaRefPattern.Match(schemaRef);
            if (!match.Success)
            {
                malformed.Add(new CheckSample($"{moduleCode}: '{schemaRef}' does not match the expected pattern"));
                continue;
            }

            if (!string.Equals(match.Groups["code"].Value, moduleCode, StringComparison.OrdinalIgnoreCase))
            {
                malformed.Add(new CheckSample($"{moduleCode}: last segment '{match.Groups["code"].Value}' does not match the lower-case ModuleCode"));
            }
        }
        sw.Stop();

        var total = SqlHelpers.Scalar(c, "SELECT COUNT(*) FROM \"mModule\"");
        var failed = nullCount + malformed.Count;
        return CheckResult.FromViolationCount(
            "A-MOD-03", ValidationPlane.A, ValidationLayer.Critical, "mModule",
            "XBRLSchemaRef never NULL, with the form .../fws/{framework}/{standard}[/{date}]/mod/{lower-case modulecode}.xsd",
            total, failed, sw.ElapsedMilliseconds, malformed);
    }

    private static CheckResult NoFrameworkVersionUrl(SqliteConnection c)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = SqlHelpers.Rows(c, "SELECT \"ModuleCode\", \"XBRLSchemaRef\" FROM \"mModule\" WHERE \"XBRLSchemaRef\" IS NOT NULL", 2);
        var offending = rows.Where(r => FrameworkVersionUrlPattern.IsMatch(r[1]!)).Select(r => new CheckSample(r[0]!)).ToList();
        sw.Stop();
        return CheckResult.FromViolationCount(
            "A-MOD-04", ValidationPlane.A, ValidationLayer.Critical, "mModule",
            "No URL of the form {framework}_{version} (no rule produces it)", rows.Count, offending.Count, sw.ElapsedMilliseconds, offending);
    }

    private static CheckResult ModuleBusinessTemplateOrderIsDenseZeroBased(SqliteConnection c)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = SqlHelpers.Rows(c, "SELECT \"ModuleID\", \"Order\" FROM \"mModuleBusinessTemplate\" ORDER BY \"ModuleID\", \"Order\"", 2);
        var failures = new List<CheckSample>();
        foreach (var group in rows.Select(r => (ModuleId: r[0]!, Order: int.Parse(r[1]!))).GroupBy(r => r.ModuleId))
        {
            var orders = group.Select(g => g.Order).OrderBy(o => o).ToList();
            var expected = Enumerable.Range(0, orders.Count).ToList();
            if (!orders.SequenceEqual(expected))
            {
                failures.Add(new CheckSample($"ModuleID={group.Key}: [{string.Join(",", orders)}]"));
            }
        }
        sw.Stop();
        return CheckResult.FromViolationCount(
            "A-MOD-07", ValidationPlane.A, ValidationLayer.Critical, "mModuleBusinessTemplate",
            "Order dense from 0 within each module, without duplicates", rows.Count, failures.Count, sw.ElapsedMilliseconds, failures);
    }

    /// <summary>
    /// A-MOD-08: <c>TaxonomyKey</c> bijection (the segment between <c>/fws/</c> and <c>/mod/</c>
    /// of <c>XBRLSchemaRef</c>): a single key per taxonomy and a single taxonomy per key.
    /// Entirely derivable from the generated database, without needing the Access.
    /// </summary>
    private static CheckResult TaxonomyKeyBijection(SqliteConnection c)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = SqlHelpers.Rows(c, "SELECT \"TaxonomyID\", \"XBRLSchemaRef\" FROM \"mModule\" WHERE \"XBRLSchemaRef\" IS NOT NULL", 2);

        var keysByTaxonomy = new Dictionary<string, HashSet<string>>();
        var taxonomiesByKey = new Dictionary<string, HashSet<string>>();
        var unparsable = new List<CheckSample>();

        foreach (var row in rows)
        {
            var taxonomyId = row[0]!;
            var schemaRef = row[1]!;
            var match = XbrlSchemaRefPattern.Match(schemaRef);
            if (!match.Success)
            {
                unparsable.Add(new CheckSample($"TaxonomyID={taxonomyId}: '{schemaRef}'"));
                continue;
            }

            var key = match.Groups["key"].Value;
            if (!keysByTaxonomy.TryGetValue(taxonomyId, out var keys))
            {
                keys = new HashSet<string>(StringComparer.Ordinal);
                keysByTaxonomy[taxonomyId] = keys;
            }

            keys.Add(key);

            if (!taxonomiesByKey.TryGetValue(key, out var taxonomies))
            {
                taxonomies = new HashSet<string>(StringComparer.Ordinal);
                taxonomiesByKey[key] = taxonomies;
            }

            taxonomies.Add(taxonomyId);
        }

        var multiKeyTaxonomies = keysByTaxonomy.Where(kv => kv.Value.Count > 1).Select(kv => new CheckSample($"TaxonomyID={kv.Key}: {kv.Value.Count} keys")).ToList();
        var sharedKeys = taxonomiesByKey.Where(kv => kv.Value.Count > 1).Select(kv => new CheckSample($"key='{kv.Key}': {kv.Value.Count} taxonomies")).ToList();
        var failures = unparsable.Concat(multiKeyTaxonomies).Concat(sharedKeys).ToList();
        sw.Stop();

        return CheckResult.FromViolationCount(
            "A-MOD-08", ValidationPlane.A, ValidationLayer.Critical, "mModule",
            "TaxonomyKey bijection: a single key per taxonomy and a single taxonomy per key",
            keysByTaxonomy.Count, failures.Count, sw.ElapsedMilliseconds, failures);
    }
}
