using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.Checks;

/// <summary>
/// B-MOD-01/02/03: module census, literal <c>ModuleLabel</c>, <c>mModuleBusinessTemplate</c> and
/// its relative <c>Order</c>, literal <c>XBRLSchemaRef</c> with declared divergences. Generalised
/// to the comparable universe.
/// </summary>
public static class ModuleCensusChecks
{
    public static IEnumerable<CheckResult> Run(
        SqliteConnection generated, SqliteConnection reference, string referenceRole, IReadOnlySet<string> taxonomies, List<KnownExceptions.Outcome> exceptionSink)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var generatedModules = ModuleRows(generated, taxonomies);
        var referenceModules = ModuleRows(reference, taxonomies);

        if (referenceModules.Count == 0)
        {
            // B-MOD-03: against the 4.2 the module comparison is EMPTY, and that is NOT a failure
            // (the 13 common taxonomies are exactly the ones that have no module there).
            yield return CheckResult.Skip(
                "B-MOD-01", ValidationPlane.B, ValidationLayer.Critical, "mModule",
                "Module census against the reference", "No module in the reference for the comparable universe (B-MOD-03: expected behaviour, not a failure)");
            yield break;
        }

        yield return PlaneBSupport.Containment(
            "B-MOD-01.1", ValidationLayer.Critical, "mModule", "Census of ModuleCode: generated contains the reference",
            "B-MOD-01-CODE", referenceRole, generatedModules.Keys.ToHashSet(StringComparer.Ordinal), referenceModules.Keys.ToHashSet(StringComparer.Ordinal), exceptionSink, sw.ElapsedMilliseconds);

        sw.Restart();
        var comparableLabel = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var (key, refRow) in referenceModules)
        {
            if (generatedModules.TryGetValue(key, out var genRow))
            {
                comparableLabel[key] = (genRow.Label, refRow.Label);
            }
        }

        yield return PlaneBSupport.LiteralWithDeclaredDivergences(
            "B-MOD-01.2", ValidationLayer.Critical, "mModule", "ModuleLabel literal against the reference",
            "B-MOD-01-LABEL", referenceRole, comparableLabel, exceptionSink, sw.ElapsedMilliseconds);

        sw.Restart();
        var comparableSchemaRef = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var (key, refRow) in referenceModules)
        {
            if (generatedModules.TryGetValue(key, out var genRow))
            {
                comparableSchemaRef[key] = (genRow.SchemaRef, refRow.SchemaRef);
            }
        }

        yield return PlaneBSupport.LiteralWithDeclaredDivergences(
            "B-MOD-02", ValidationLayer.Informative, "mModule", "XBRLSchemaRef literal, with declared divergences",
            "B-MOD-02", referenceRole, comparableSchemaRef, exceptionSink, sw.ElapsedMilliseconds);

        sw.Restart();
        var generatedBt = ModuleBusinessTemplateSequences(generated, taxonomies);
        var referenceBt = ModuleBusinessTemplateSequences(reference, taxonomies);
        var failures = new List<CheckSample>();
        foreach (var (key, refSequence) in referenceBt)
        {
            if (!generatedBt.TryGetValue(key, out var genSequence))
            {
                failures.Add(new CheckSample($"{key}: module absent from the generated output"));
                continue;
            }

            if (!genSequence.SequenceEqual(refSequence, StringComparer.Ordinal))
            {
                failures.Add(new CheckSample(key, string.Join("·", genSequence), string.Join("·", refSequence)));
            }
        }
        sw.Stop();

        yield return CheckResult.FromViolationCount(
            "B-MOD-01.3", ValidationPlane.B, ValidationLayer.Critical, "mModuleBusinessTemplate",
            "mModuleBusinessTemplate: relative sequence of Order per module", referenceBt.Count, failures.Count, sw.ElapsedMilliseconds, failures);
    }

    private sealed record ModuleRow(string Label, string SchemaRef);

    private static Dictionary<string, ModuleRow> ModuleRows(SqliteConnection c, IReadOnlySet<string> taxonomies)
    {
        var rows = SqlHelpers.Rows(
            c,
            """
            SELECT t."TaxonomyCode", m."ModuleCode", m."ModuleLabel", m."XBRLSchemaRef"
            FROM "mModule" m JOIN "mTaxonomy" t ON t."TaxonomyID" = m."TaxonomyID"
            """,
            4);

        var result = new Dictionary<string, ModuleRow>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var taxonomy = Normalization.TaxonomyCode(row[0]!);
            if (taxonomies.Contains(taxonomy))
            {
                result[taxonomy + "|" + row[1]] = new ModuleRow(row[2] ?? "", row[3] ?? "");
            }
        }

        return result;
    }

    private static Dictionary<string, List<string>> ModuleBusinessTemplateSequences(SqliteConnection c, IReadOnlySet<string> taxonomies)
    {
        var rows = SqlHelpers.Rows(
            c,
            """
            SELECT t."TaxonomyCode", m."ModuleCode", mbt."Order", tot."TemplateOrTableCode"
            FROM "mModuleBusinessTemplate" mbt
            JOIN "mModule" m ON m."ModuleID" = mbt."ModuleID"
            JOIN "mTaxonomy" t ON t."TaxonomyID" = m."TaxonomyID"
            JOIN "mTemplateOrTable" tot ON tot."TemplateOrTableID" = mbt."BusinessTemplateID"
            """,
            4);

        var byKey = new Dictionary<string, List<(int Order, string Code)>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var taxonomy = Normalization.TaxonomyCode(row[0]!);
            if (!taxonomies.Contains(taxonomy))
            {
                continue;
            }

            var key = taxonomy + "|" + row[1];
            if (!byKey.TryGetValue(key, out var list))
            {
                list = [];
                byKey[key] = list;
            }

            list.Add((int.Parse(row[2]!), Normalization.StripAnyTableGroupPrefix(row[3]!)));
        }

        return byKey.ToDictionary(kv => kv.Key, kv => Normalization.RelativeOrder(kv.Value).ToList());
    }
}
