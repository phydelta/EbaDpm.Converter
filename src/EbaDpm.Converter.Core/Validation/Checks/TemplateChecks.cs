using EbaDpm.Converter.Core.Mapping.Dpm20;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.Checks;

/// <summary>
/// Templates and tables (<c>mTemplateOrTable</c>, <c>mTable</c>, <c>mTaxonomyTable</c>).
/// Plane A, without looking at any reference.
///
/// A-TPL-12 (<c>mTaxonomyTable</c> is NOT 1:1 with <c>mTable</c>) is an ANTI-invariant: it is
/// deliberately not checked here.
///
/// <c>A-TPL-07.2</c> used to compare <c>Order</c> against <c>StringComparer.Ordinal</c> of
/// <c>TemplateOrTableCode</c>. That is not a pinned census (there is no literal number), but it is
/// the same disease in another form: a property verified only against the code range of one
/// particular publication. `StringComparer.Ordinal` coincides with the NATURAL (numeric) order as
/// long as no group mixes codes with a different number of digits in the same segment (`C_9` vs
/// `C_10` is already avoided by length, but `C_11` vs `C_101` is not), and that only happens in
/// 4.3, whose group of orphan <c>BusinessTable</c> rows (<c>ParentTemplateOrTableID</c>=0) grew
/// from 48 to 802 rows and finally contains both. <c>Order</c> is assigned by
/// <c>Dpm20StructureLoader.AssignNaturalOrder</c> with
/// <see cref="Dpm20StructureLoader.NaturalStringComparer"/> (digits compared as a number, not
/// character by character); measured: it reproduces 100% of the 802 rows. The check must verify
/// the SAME property that the pipeline guarantees, not an approximation that only coincided with
/// it for the code range of the 4.2.
/// </summary>
public static class TemplateChecks
{
    public static IEnumerable<CheckResult> Run(SqliteConnection c)
    {
        yield return SqlHelpers.CountCheck(
            c, "A-TPL-01", ValidationLayer.Critical, "mTemplateOrTable", "TemplateOrTableType ∈ {TableGroup, BusinessTable}; Level ∈ {1,2}",
            "SELECT COUNT(*) FROM \"mTemplateOrTable\"",
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" NOT IN ('TableGroup','BusinessTable') OR \"Level\" NOT IN (1,2)");

        yield return SqlHelpers.CountCheck(
            c, "A-TPL-02", ValidationLayer.Critical, "mTemplateOrTable", "Every TableGroup L1 has ParentTemplateOrTableID=0; no NULL row",
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\"='TableGroup' AND \"Level\"=1",
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\"='TableGroup' AND \"Level\"=1 AND (\"ParentTemplateOrTableID\" IS NULL OR \"ParentTemplateOrTableID\" <> 0)");

        yield return SqlHelpers.CountCheck(
            c, "A-TPL-03", ValidationLayer.Critical, "mTemplateOrTable", "Every TableGroup L2 has an existing TableGroup/L1 parent",
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\"='TableGroup' AND \"Level\"=2",
            """
            SELECT COUNT(*) FROM "mTemplateOrTable" c
            WHERE c."TemplateOrTableType"='TableGroup' AND c."Level"=2
              AND NOT EXISTS (SELECT 1 FROM "mTemplateOrTable" p WHERE p."TemplateOrTableID" = c."ParentTemplateOrTableID" AND p."TemplateOrTableType"='TableGroup' AND p."Level"=1)
            """);

        yield return SqlHelpers.CountCheck(
            c, "A-TPL-04.1", ValidationLayer.Critical, "mTemplateOrTable", "Every BusinessTable has an existing TableGroup/L2 parent (excluding the unresolved 0)",
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\"='BusinessTable'",
            """
            SELECT COUNT(*) FROM "mTemplateOrTable" c
            WHERE c."TemplateOrTableType"='BusinessTable' AND c."ParentTemplateOrTableID" <> 0
              AND NOT EXISTS (SELECT 1 FROM "mTemplateOrTable" p WHERE p."TemplateOrTableID" = c."ParentTemplateOrTableID" AND p."TemplateOrTableType"='TableGroup' AND p."Level"=2)
            """);
        yield return SqlHelpers.CountCheck(
            c, "A-TPL-04.2", ValidationLayer.Critical, "mTemplateOrTable", "No BusinessTable with ParentTemplateOrTableID NULL",
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\"='BusinessTable'",
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\"='BusinessTable' AND \"ParentTemplateOrTableID\" IS NULL");

        // "The parent of an mTemplateOrTable node belongs to the same taxonomy" is already A-TPL-05.
        yield return SqlHelpers.CountCheck(
            c, "A-TPL-05", ValidationLayer.Critical, "mTemplateOrTable", "The parent always belongs to the same taxonomy",
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"ParentTemplateOrTableID\" IS NOT NULL AND \"ParentTemplateOrTableID\" <> 0",
            """
            SELECT COUNT(*) FROM "mTemplateOrTable" c
            JOIN "mTemplateOrTable" p ON p."TemplateOrTableID" = c."ParentTemplateOrTableID"
            WHERE c."TaxonomyID" <> p."TaxonomyID"
            """);

        yield return SqlHelpers.CountCheck(
            c, "A-TPL-06", ValidationLayer.Critical, "mTemplateOrTable", "Order of TableGroup L1 is constant 0",
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\"='TableGroup' AND \"Level\"=1",
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\"='TableGroup' AND \"Level\"=1 AND \"Order\" IS NOT 0");

        yield return OrderIsDenseWithinParent(c, "A-TPL-07.1", "TableGroup", 2);
        yield return OrderIsDenseWithinParent(c, "A-TPL-07.2", "BusinessTable", null);

        yield return SqlHelpers.CountCheck(
            c, "A-TPL-08", ValidationLayer.Critical, "mTemplateOrTable", "IsTableGroupSource = 1 in 100% of rows",
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\"='TableGroup'",
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\"='TableGroup' AND \"IsTableGroupSource\" IS NOT 1");

        yield return SqlHelpers.CountCheck(
            c, "A-TPL-09.1", ValidationLayer.Critical, "mTaxonomyTable", "IsSimplyReuse constant true",
            "SELECT COUNT(*) FROM \"mTaxonomyTable\"", "SELECT COUNT(*) FROM \"mTaxonomyTable\" WHERE \"IsSimplyReuse\" IS NOT 1");
        yield return SqlHelpers.CountCheck(
            c, "A-TPL-09.2", ValidationLayer.Critical, "mTaxonomyTable", "IsTableSource constant true",
            "SELECT COUNT(*) FROM \"mTaxonomyTable\"", "SELECT COUNT(*) FROM \"mTaxonomyTable\" WHERE \"IsTableSource\" IS NOT 1");

        yield return SqlHelpers.CountCheck(
            c, "A-TPL-10", ValidationLayer.Critical, "mTaxonomyTable", "AnnotatedTableID always points to a BusinessTable",
            "SELECT COUNT(*) FROM \"mTaxonomyTable\"",
            """
            SELECT COUNT(*) FROM "mTaxonomyTable" tt
            JOIN "mTemplateOrTable" tot ON tot."TemplateOrTableID" = tt."AnnotatedTableID"
            WHERE tot."TemplateOrTableType" <> 'BusinessTable'
            """);

        yield return SqlHelpers.CountCheck(
            c, "A-TPL-11.1", ValidationLayer.Critical, "mTable", "No mTable table is left without mTaxonomyTable",
            "SELECT COUNT(*) FROM \"mTable\"",
            "SELECT COUNT(*) FROM \"mTable\" t WHERE NOT EXISTS (SELECT 1 FROM \"mTaxonomyTable\" tt WHERE tt.\"TableID\" = t.\"TableID\")");
        yield return SqlHelpers.CountCheck(
            c, "A-TPL-11.2", ValidationLayer.Critical, "mTaxonomy", "No taxonomy is left without tables",
            "SELECT COUNT(*) FROM \"mTaxonomy\"",
            "SELECT COUNT(*) FROM \"mTaxonomy\" t WHERE NOT EXISTS (SELECT 1 FROM \"mTaxonomyTable\" tt WHERE tt.\"TaxonomyID\" = t.\"TaxonomyID\")");

        yield return SqlHelpers.CountCheck(
            c, "A-TPL-13", ValidationLayer.Informative, "mTable", "mTable.JsonBlob is a zero-length BLOB, not NULL",
            "SELECT COUNT(*) FROM \"mTable\"",
            "SELECT COUNT(*) FROM \"mTable\" WHERE \"JsonBlob\" IS NULL OR length(\"JsonBlob\") <> 0");

        yield return SqlHelpers.CountCheck(
            c, "A-TPL-14", ValidationLayer.Critical, "mReportingFramework", "No orphan framework: used by >=1 mTaxonomy",
            "SELECT COUNT(*) FROM \"mReportingFramework\"",
            "SELECT COUNT(*) FROM \"mReportingFramework\" f WHERE NOT EXISTS (SELECT 1 FROM \"mTaxonomy\" t WHERE t.\"FrameworkID\" = f.\"FrameworkID\")");

        yield return SqlHelpers.CountCheck(
            c, "A-TPL-15", ValidationLayer.Critical, "mTaxonomy", "Every mTaxonomy has an existing FrameworkID",
            "SELECT COUNT(*) FROM \"mTaxonomy\"",
            "SELECT COUNT(*) FROM \"mTaxonomy\" t WHERE NOT EXISTS (SELECT 1 FROM \"mReportingFramework\" f WHERE f.\"FrameworkID\" = t.\"FrameworkID\")");
    }

    /// <summary>
    /// A-TPL-07: <c>Order</c> dense <c>0..n-1</c> within each parent, matching the NATURAL order of
    /// <c>TemplateOrTableCode</c>. <paramref name="level"/> filters by level (TableGroup L2) or is
    /// ignored if <c>null</c> (BusinessTable, which has only one possible level).
    /// </summary>
    private static CheckResult OrderIsDenseWithinParent(SqliteConnection c, string id, string type, int? level)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var levelFilter = level is { } l ? $" AND \"Level\" = {l}" : string.Empty;
        var rows = SqlHelpers.Rows(
            c,
            $"SELECT \"ParentTemplateOrTableID\", \"Order\", \"TemplateOrTableCode\" FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = '{type}'{levelFilter}",
            3);

        var failures = new List<CheckSample>();
        foreach (var group in rows.GroupBy(r => r[0]))
        {
            var byOrder = group.Select(r => (Order: int.Parse(r[1]!), Code: r[2]!)).OrderBy(x => x.Order).ToList();
            var expectedOrders = Enumerable.Range(0, byOrder.Count).ToList();
            if (!byOrder.Select(x => x.Order).SequenceEqual(expectedOrders))
            {
                failures.Add(new CheckSample($"Parent={group.Key}: Order not dense 0..n-1"));
                continue;
            }

            // NATURAL (numeric) order, the same comparer the pipeline uses
            // (Dpm20StructureLoader.AssignNaturalOrder) to assign Order. NOT
            // StringComparer.Ordinal, which breaks as soon as two codes of the same group carry a
            // numeric segment with a different number of digits ("C_11.00" vs "C_101.00").
            var byCodeNatural = byOrder.Select(x => x.Code).OrderBy(code => code, Dpm20StructureLoader.NaturalStringComparer.Instance).ToList();
            if (!byOrder.Select(x => x.Code).SequenceEqual(byCodeNatural))
            {
                failures.Add(new CheckSample($"Parent={group.Key}: Order does not match the NATURAL (numeric) order of TemplateOrTableCode"));
            }
        }
        sw.Stop();

        return CheckResult.FromViolationCount(
            id, ValidationPlane.A, ValidationLayer.Critical, "mTemplateOrTable",
            $"Order of {type} is dense 0..n-1 per parent and matches the NATURAL (numeric) order of TemplateOrTableCode",
            rows.Count, failures.Count, sw.ElapsedMilliseconds, failures);
    }
}
