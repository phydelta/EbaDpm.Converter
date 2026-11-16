using EbaDpm.Converter.Tests.Dictionary;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Infrastructure;

/// <summary>
/// Named, test-side known exception for the plane B comparison of a generated 4.2 file against the
/// 4.2 reference SQLite export (information only, never wins a tie): the taxonomy <c>dora 4.2</c>
/// and its framework <c>DORA</c> are emitted since #75 (the Access database declares them) and the
/// reference export does not contain them. The exception excludes exactly these business keys,
/// by name, and is STALE-checked: the AssertStale methods
/// fails when the excluded objects are not in the generated side or are in the reference.
/// It is never a threshold and never a pattern.
/// </summary>
internal static class DoraAbsentFromReference42
{
    public const string TaxonomyCode = "dora 4.2";

    public const string FrameworkCode = "DORA";

    public const string Reason =
        "#75: DORA is emitted from the Access database (taxonomy 'dora 4.2', framework 'DORA'); the 4.2 reference export has no DORA.";

    /// <summary>
    /// SQL (generated file) yielding the <c>TableID</c>s of the tables that belong to the taxonomy
    /// <see cref="TaxonomyCode"/>, derived from <c>mTaxonomyTable</c>; no table code is typed by hand.
    /// </summary>
    public const string DoraTableIdsSql =
        "SELECT tt.\"TableID\" FROM \"mTaxonomyTable\" tt JOIN \"mTaxonomy\" x ON x.\"TaxonomyID\" = tt.\"TaxonomyID\" "
        + "WHERE x.\"TaxonomyCode\" = 'dora 4.2'";

    /// <summary>Stale check of the taxonomy: in the generated (or derived) side, absent from the reference.</summary>
    public static void AssertTaxonomyStale(IReadOnlySet<string> generatedTaxonomyCodes, IReadOnlySet<string> referenceTaxonomyCodes)
    {
        Assert.True(
            generatedTaxonomyCodes.Contains(TaxonomyCode),
            $"Stale known exception ({Reason}): taxonomy '{TaxonomyCode}' is not in the generated side.");
        Assert.True(
            !referenceTaxonomyCodes.Contains(TaxonomyCode),
            $"Stale known exception ({Reason}): taxonomy '{TaxonomyCode}' IS in the reference export; the exception no longer applies.");
    }

    /// <summary>Stale check of the framework: in the generated side, absent from the reference.</summary>
    public static void AssertFrameworkStale(IReadOnlySet<string> generatedFrameworkCodes, IReadOnlySet<string> referenceFrameworkCodes)
    {
        Assert.True(
            generatedFrameworkCodes.Contains(FrameworkCode),
            $"Stale known exception ({Reason}): framework '{FrameworkCode}' is not in the generated side.");
        Assert.True(
            !referenceFrameworkCodes.Contains(FrameworkCode),
            $"Stale known exception ({Reason}): framework '{FrameworkCode}' IS in the reference export; the exception no longer applies.");
    }

    /// <summary>Stale check for the connection-level case (generated file and reference file).</summary>
    public static void AssertStale(SqliteConnection generated, SqliteConnection reference)
    {
        AssertTaxonomyStale(
            Column(generated, "SELECT \"TaxonomyCode\" FROM \"mTaxonomy\""),
            Column(reference, "SELECT \"TaxonomyCode\" FROM \"mTaxonomy\""));
        AssertFrameworkStale(
            Column(generated, "SELECT \"FrameworkCode\" FROM \"mReportingFramework\""),
            Column(reference, "SELECT \"FrameworkCode\" FROM \"mReportingFramework\""));
    }

    /// <summary>
    /// The tables the exception excludes from the open-axis comparison: derived from the generated
    /// <c>mTaxonomyTable</c>, asserted non-empty (stale otherwise) and absent from the reference <c>mTable</c>
    /// by table code.
    /// </summary>
    public static IReadOnlyList<string> AssertStaleTables(SqliteConnection generated, SqliteConnection reference)
    {
        var codes = QueryHelpers.Rows(
                generated,
                $"SELECT DISTINCT t.\"TableCode\" FROM \"mTable\" t WHERE t.\"TableID\" IN ({DoraTableIdsSql})",
                1)
            .Select(r => r[0]!)
            .ToList();
        Assert.True(codes.Count > 0, $"Stale known exception ({Reason}): taxonomy '{TaxonomyCode}' has no table in the generated mTaxonomyTable.");

        var inReference = Column(reference, "SELECT \"TableCode\" FROM \"mTable\"");
        var present = codes.Where(inReference.Contains).ToList();
        Assert.True(
            present.Count == 0,
            $"Stale known exception ({Reason}): tables of '{TaxonomyCode}' present in the reference export: {string.Join(", ", present)}.");
        return codes;
    }

    private static HashSet<string> Column(SqliteConnection connection, string sql)
        => QueryHelpers.Rows(connection, sql, 1).Where(r => r[0] is not null).Select(r => r[0]!).ToHashSet(StringComparer.Ordinal);
}
