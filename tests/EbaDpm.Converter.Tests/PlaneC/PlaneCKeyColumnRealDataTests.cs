using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Core.Validation.PlaneC;
using EbaDpm.Converter.Tests.Dpm2;
using Microsoft.Data.Sqlite;
using EbaDpm.Converter.Tests.Support;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// The control that really matters, measured on real data: of the signatures that
/// <c>PlaneCComparer.IsOpenAxisKeyColumnSignature</c> excludes for being the key column of an open
/// axis, <b>0 were CONTAINED before excluding them</b> -- the exclusion does not take from
/// <c>C-DPS-01</c> a single signature that would match today (zero detection cost).
///
/// <c>IsOpenAxisKeyColumnSignature</c> is <c>private</c> inside <c>PlaneCComparer</c> -- there is
/// no way to "switch it off" from outside to measure what would happen without it. This test does
/// NOT use reflection on the internal (fragile, and no other test in this project does): it
/// re-implements the SAME rule, INDEPENDENTLY, from the two PUBLIC pieces that
/// <c>PlaneCComparer</c> also uses -- <c>PlaneCLayoutReader.Read</c> (layout signatures) and
/// <c>PlaneCSignature</c> (normalization) -- and checks, with its OWN containment logic, whether
/// those signatures would appear in our real output. This is stronger than instrumenting the
/// internal: it is a second implementation verifying the same property, not the same
/// implementation asking itself.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class PlaneCKeyColumnRealDataTests(Dpm20SkeletonFixture fixture)
{
    [DataFact]
    public void CKey01_OfTheExcludedKeyColumnSignatures_NoneWasContainedBefore_MeasuredIndependently()
    {
        using var generated = OpenReadOnly(fixture.ValidatedDatabasePath);
        using var layouts = OpenReadOnly(PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);

        var layoutData = PlaneCLayoutReader.Read(layouts);

        // Independent re-implementation of PlaneCComparer.IsOpenAxisKeyColumnSignature -- same
        // shape, built from the PUBLIC functions of PlaneCSignature.
        var excluded = layoutData.Signatures
            .Where(s => IsOpenAxisKeyColumnSignatureIndependent(s.NormalizedTerms))
            .Select(s => (s.TableCode, Canonical: PlaneCSignature.Canonicalize(s.NormalizedTerms), Terms: s.NormalizedTerms))
            .GroupBy(x => (x.TableCode, x.Canonical))
            .Select(g => g.First())
            .ToList();

        // Universe positive control: 22 were measured on the 4.2 corpus -- if this came out 0, the
        // re-implementation (or the reading of the repository) would be broken, and the rest of
        // the measurement would prove nothing.
        Assert.True(excluded.Count > 0, "0 signatures recognized as key column -- empty universe.");

        // OUR signatures: union by TableCode of all the TableIDs that share a code, rebuilt with
        // the SAME public normalization that PlaneCComparer uses.
        var tableCodeByTableId = SqlHelpers.Rows(generated, """SELECT "TableID", "TableCode" FROM "mTable" WHERE "TableCode" IS NOT NULL""", 2)
            .ToDictionary(r => long.Parse(r[0]!), r => r[1]!);

        var ourSignaturesByTableCode = new Dictionary<string, List<HashSet<string>>>(StringComparer.Ordinal);
        foreach (var row in SqlHelpers.Rows(generated, """SELECT "TableID", "DPS" FROM "mTableCell" WHERE "DPS" IS NOT NULL""", 2))
        {
            var tableId = long.Parse(row[0]!);
            if (!tableCodeByTableId.TryGetValue(tableId, out var tableCode))
            {
                continue;
            }

            var terms = PlaneCSignature.SplitAndNormalize(row[1]!);
            if (terms.Count == 0)
            {
                continue;
            }

            if (!ourSignaturesByTableCode.TryGetValue(tableCode, out var list))
            {
                list = [];
                ourSignaturesByTableCode[tableCode] = list;
            }

            list.Add(new HashSet<string>(terms, StringComparer.Ordinal));
        }

        // "Contained" = there is a signature of ours that INCLUDES (is a superset of) all the terms
        // of the layout signature -- exactly the containment criterion, re-implemented.
        var wouldHaveBeenContained = excluded
            .Where(ex => ourSignaturesByTableCode.TryGetValue(ex.TableCode, out var signatures)
                && signatures.Any(f => ex.Terms.All(f.Contains)))
            .ToList();

        Assert.True(
            wouldHaveBeenContained.Count == 0,
            "FINDING: the key-column exclusion DOES take from C-DPS-01 a signature that " +
            $"would match today -- {wouldHaveBeenContained.Count} of {excluded.Count}: " +
            string.Join(" | ", wouldHaveBeenContained.Select(x => $"{x.TableCode}: {x.Canonical}")));
    }

    /// <summary>Independent copy (not a call) of <c>PlaneCComparer.IsOpenAxisKeyColumnSignature</c> --
    /// deliberately rewritten from the statement of the rule, not copied character by character
    /// from the source, so that an error shared by the two does not cancel itself out.</summary>
    private static bool IsOpenAxisKeyColumnSignatureIndependent(IReadOnlyList<string> normalizedTerms)
    {
        var metMembers = normalizedTerms
            .Where(t => PlaneCSignature.DimensionOf(t) == "MET")
            .Select(PlaneCSignature.MemberOf)
            .ToList();

        if (metMembers.Count != 1 || string.IsNullOrEmpty(metMembers[0]))
        {
            return false;
        }

        var sentinel = metMembers[0] + "(*)";
        return normalizedTerms.Contains(sentinel, StringComparer.Ordinal);
    }

    /// <summary>Surface property (not the exact figure): C-KEY-01 excludes a positive number of
    /// signatures, in more than one table -- 22 in 16 tables were measured on the 4.2 corpus.</summary>
    [DataFact]
    public void CKey01_OnTheRealDpm2042Corpus_ExcludesAPositiveSetInSeveralTables()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);

        var cKey01 = Assert.Single(result.Report.Checks, c => c.Id == "C-KEY-01");

        Assert.True(cKey01.Examined > 0, "C-KEY-01 examined 0 key columns on the real corpus -- 22 were measured.");
        var tablesNamed = cKey01.Samples.Select(s => s.BusinessKey.Split(' ')[0]).Distinct().ToList();
        Assert.True(tablesNamed.Count > 1, $"C-KEY-01 names only {tablesNamed.Count} table(s) -- 16 were measured.");

        // The exclusion reduces SignaturesCompared of C-DPS-01/C-COB-01 by exactly the same amount
        // it excludes -- it must not appear duplicated or lost in the coverage.
        var coverage = Assert.Single(result.Report.Checks, c => c.Id == "C-COB-01");
        Assert.Contains($"{cKey01.Examined} signature(s) excluded for being THE KEY COLUMN", coverage.Statement);
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        return connection;
    }
}
