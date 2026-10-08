using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.Checks;

/// <summary>
/// B-TPL-01/02/03: coverage of the template and table tree (<c>mTemplateOrTable</c>,
/// <c>mTable</c>) against the reference, by business key and with the <c>eba_tg</c>/<c>tg</c>
/// prefix stripped. Generalised to the whole comparable universe instead of a single fixed
/// taxonomy.
/// </summary>
public static class TemplateCensusChecks
{
    public static IEnumerable<CheckResult> Run(
        SqliteConnection generated, SqliteConnection reference, string referenceRole, IReadOnlySet<string> taxonomies, List<KnownExceptions.Outcome> exceptionSink)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var generatedL1 = Level1Keys(generated, taxonomies);
        var referenceL1 = Level1Keys(reference, taxonomies);
        yield return PlaneBSupport.Containment(
            "B-TPL-01.1", ValidationLayer.Critical, "mTemplateOrTable", "Census of TableGroup L1 (without prefix): generated contains the reference",
            "B-TPL-01-L1-CODE", referenceRole, generatedL1, referenceL1, exceptionSink, sw.ElapsedMilliseconds);

        sw.Restart();
        var generatedL2 = Level2Pairs(generated, taxonomies);
        var referenceL2 = Level2Pairs(reference, taxonomies);
        yield return PlaneBSupport.Containment(
            "B-TPL-01.2", ValidationLayer.Critical, "mTemplateOrTable", "Census of L2->L1 pairs (without prefix): generated contains the reference",
            "B-TPL-01-L2-PAIR", referenceRole, generatedL2, referenceL2, exceptionSink, sw.ElapsedMilliseconds);

        sw.Restart();
        var generatedBt = BusinessTableCodes(generated, taxonomies);
        var referenceBt = BusinessTableCodes(reference, taxonomies);
        yield return PlaneBSupport.Containment(
            "B-TPL-01.3", ValidationLayer.Critical, "mTemplateOrTable", "Census of BusinessTable: generated contains the reference",
            "B-TPL-01-BUSINESSTABLE-CODE", referenceRole, generatedBt, referenceBt, exceptionSink, sw.ElapsedMilliseconds);

        sw.Restart();
        var generatedTables = BusinessKeys.BuildTableIdsByBusinessKey(generated, taxonomies).Keys.Select(k => k.Taxonomy + "|" + k.TableCode).ToHashSet(StringComparer.Ordinal);
        var referenceTables = BusinessKeys.BuildTableIdsByBusinessKey(reference, taxonomies).Keys.Select(k => k.Taxonomy + "|" + k.TableCode).ToHashSet(StringComparer.Ordinal);
        yield return PlaneBSupport.Containment(
            "B-TPL-03", ValidationLayer.Critical, "mTable", "Census of mTable.TableCode (per taxonomy): generated contains the reference",
            "B-TPL-03-TABLE-CODE", referenceRole, generatedTables, referenceTables, exceptionSink, sw.ElapsedMilliseconds);
    }

    private static HashSet<string> Level1Keys(SqliteConnection c, IReadOnlySet<string> taxonomies)
    {
        var rows = SqlHelpers.Rows(
            c,
            """
            SELECT t."TaxonomyCode", tot."TemplateOrTableCode" FROM "mTemplateOrTable" tot
            JOIN "mTaxonomy" t ON t."TaxonomyID" = tot."TaxonomyID"
            WHERE tot."TemplateOrTableType" = 'TableGroup' AND tot."Level" = 1
            """,
            2);

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var taxonomy = Normalization.TaxonomyCode(row[0]!);
            if (!taxonomies.Contains(taxonomy))
            {
                continue;
            }

            result.Add(taxonomy + "|" + Normalization.StripAnyTableGroupPrefix(row[1]!));
        }

        return result;
    }

    private static HashSet<string> Level2Pairs(SqliteConnection c, IReadOnlySet<string> taxonomies)
    {
        var rows = SqlHelpers.Rows(
            c,
            """
            SELECT t."TaxonomyCode", c."TemplateOrTableCode", p."TemplateOrTableCode"
            FROM "mTemplateOrTable" c
            JOIN "mTaxonomy" t ON t."TaxonomyID" = c."TaxonomyID"
            JOIN "mTemplateOrTable" p ON p."TemplateOrTableID" = c."ParentTemplateOrTableID"
            WHERE c."TemplateOrTableType" = 'TableGroup' AND c."Level" = 2
            """,
            3);

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var taxonomy = Normalization.TaxonomyCode(row[0]!);
            if (!taxonomies.Contains(taxonomy))
            {
                continue;
            }

            var child = Normalization.StripAnyTableGroupPrefix(row[1]!);
            var parent = Normalization.StripAnyTableGroupPrefix(row[2]!);
            result.Add(taxonomy + "|" + child + "|" + parent);
        }

        return result;
    }

    private static HashSet<string> BusinessTableCodes(SqliteConnection c, IReadOnlySet<string> taxonomies)
    {
        var rows = SqlHelpers.Rows(
            c,
            """
            SELECT t."TaxonomyCode", tot."TemplateOrTableCode" FROM "mTemplateOrTable" tot
            JOIN "mTaxonomy" t ON t."TaxonomyID" = tot."TaxonomyID"
            WHERE tot."TemplateOrTableType" = 'BusinessTable'
            """,
            2);

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var taxonomy = Normalization.TaxonomyCode(row[0]!);
            if (taxonomies.Contains(taxonomy))
            {
                result.Add(taxonomy + "|" + row[1]);
            }
        }

        return result;
    }
}
