using System.Globalization;
using EbaDpm.Converter.Distribution.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Distribution.Tests.Dpm2;

/// <summary>
/// Independent derivation, straight from the Access tables with OLEDB and WITHOUT calling production
/// code, of the <c>mHierarchyNode</c> nodes that the tie-break by consistency re-anchors
/// (docs/mapping-dpm2.md, "Tie-break by consistency"): the nodes of the EFFECTIVE version of each
/// subcategory whose resolved member would belong neither to the domain of its hierarchy nor to a
/// domain united to it, and for which the older <c>ItemCategory</c> row with the highest
/// <c>StartReleaseID &lt;= R</c> in an allowed domain gives the member.
///
/// It is derived from the documented rule, not from the loader's code: (1) item category current at the
/// release of the emitted subcategory version (else the one with the highest start); (2) same-code twin
/// anchoring when the twin has the same label; (3) if still outside the allowed domains, the tie-break.
/// The ONLY thing read from the generated SQLite file is <c>mMember</c> (does a member
/// <c>DomainCode:MemberCode</c> exist, and its label), because which codes become members (the
/// <c>_PR</c> filter) is the dictionary's decision, not this rule's.
/// </summary>
internal static class HierarchyNodeConsistencyOracle
{
    /// <summary>One node the tie-break must re-anchor, by business key.</summary>
    public sealed record Case(
        string HierarchyCode,
        string HierarchyDomainCode,
        int ItemId,
        string NaturalMemberKey,
        IReadOnlyList<string> ExpectedMemberKeys);

    public static IReadOnlyList<Case> Derive(string accdb, string releaseCode, string generatedSqlitePath)
    {
        var releaseId = AccessCensus.ReleaseId(accdb, releaseCode);
        var r = releaseId.ToString(CultureInfo.InvariantCulture);

        static int I(object o) => Convert.ToInt32(o, CultureInfo.InvariantCulture);
        static int? N(object o) => o is DBNull ? null : Convert.ToInt32(o, CultureInfo.InvariantCulture);

        string? DomainCode(Dictionary<int, string> categoryCodeById, int categoryId)
            => categoryCodeById.TryGetValue(categoryId, out var code) ? (code == "_PR" ? "MET" : code) : null;

        var categoryCodeById = AccessCensus.Rows(accdb, $"SELECT [CategoryID], [Code] FROM [Category] WHERE [CreatedRelease] <= {r}")
            .ToDictionary(row => I(row[0]), row => Convert.ToString(row[1], CultureInfo.InvariantCulture)!);

        var united = AccessCensus.Rows(accdb, $"SELECT [SuperCategoryID], [CategoryID] FROM [SuperCategoryComposition] WHERE [StartReleaseID] <= {r}")
            .Select(row => (Union: DomainCode(categoryCodeById, I(row[0])), United: DomainCode(categoryCodeById, I(row[1]))))
            .Where(p => p.Union is not null && p.United is not null)
            .Select(p => (p.Union!, p.United!))
            .ToHashSet();

        var itemCategories = AccessCensus.Rows(accdb, $"SELECT [ItemID], [CategoryID], [Code], [StartReleaseID], [EndReleaseID] FROM [ItemCategory] WHERE [StartReleaseID] <= {r}")
            .Select(row => (ItemId: I(row[0]), CategoryId: I(row[1]), Code: Convert.ToString(row[2], CultureInfo.InvariantCulture)!, Start: I(row[3]), End: N(row[4])))
            .GroupBy(x => x.ItemId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var subCategories = AccessCensus.Rows(accdb, "SELECT [SubCategoryID], [CategoryID], [Code] FROM [SubCategory]")
            .ToDictionary(row => I(row[0]), row => (CategoryId: I(row[1]), Code: Convert.ToString(row[2], CultureInfo.InvariantCulture)!));

        var versions = AccessCensus.Rows(accdb, $"SELECT [SubCategoryVID], [SubCategoryID], [StartReleaseID], [EndReleaseID] FROM [SubCategoryVersion] WHERE [StartReleaseID] <= {r}")
            .Select(row => (Vid: I(row[0]), SubCategoryId: I(row[1]), Start: I(row[2]), End: N(row[3])))
            .GroupBy(v => v.SubCategoryId);

        // Effective version of each subcategory and the release it represents.
        var effective = new Dictionary<int, (int Vid, int Release)>();
        foreach (var g in versions)
        {
            var current = g.Where(v => v.Start <= releaseId && (v.End is null || v.End.Value > releaseId)).ToList();
            if (current.Count > 0)
            {
                effective[g.Key] = (current[0].Vid, releaseId);
            }
            else
            {
                var last = g.OrderByDescending(v => v.Start).First();
                effective[g.Key] = (last.Vid, last.Start);
            }
        }

        var subCategoryByVid = effective.ToDictionary(kv => kv.Value.Vid, kv => kv.Key);
        var nodes = AccessCensus.Rows(accdb, "SELECT [ItemID], [SubCategoryVID] FROM [SubCategoryItem]")
            .Select(row => (ItemId: I(row[0]), Vid: I(row[1])))
            .Where(n => subCategoryByVid.ContainsKey(n.Vid))
            .ToList();

        var memberLabelByKey = ReadMemberLabels(generatedSqlitePath);

        var cases = new List<Case>();
        foreach (var (itemId, vid) in nodes)
        {
            var subCategoryId = subCategoryByVid[vid];
            var release = effective[subCategoryId].Release;
            var (hierarchyCategoryId, hierarchyCode) = subCategories[subCategoryId];
            var hierarchyDomain = DomainCode(categoryCodeById, hierarchyCategoryId);
            if (hierarchyDomain is null || !itemCategories.TryGetValue(itemId, out var rows))
            {
                continue;
            }

            bool Allowed(string domain) => domain == hierarchyDomain || united.Contains((hierarchyDomain, domain));

            var current = rows.FirstOrDefault(x => x.Start <= release && (x.End is null || x.End.Value > release));
            var natural = current != default ? current : rows.OrderByDescending(x => x.Start).First();
            var naturalDomain = DomainCode(categoryCodeById, natural.CategoryId);
            if (naturalDomain is null)
            {
                continue;
            }

            var naturalKey = naturalDomain + ":" + natural.Code;
            var resultDomain = naturalDomain;

            // Rule 2, the twin anchoring (same code, same label, not united).
            if (naturalDomain != hierarchyDomain
                && memberLabelByKey.TryGetValue(hierarchyDomain + ":" + natural.Code, out var twinLabel)
                && memberLabelByKey.TryGetValue(naturalKey, out var naturalLabel)
                && twinLabel == naturalLabel
                && !united.Contains((hierarchyDomain, naturalDomain)))
            {
                resultDomain = hierarchyDomain;
            }

            if (Allowed(resultDomain))
            {
                continue;
            }

            // Rule 3: the tie-break. Candidates at the highest StartReleaseID <= R in an allowed domain with a member.
            var candidates = rows
                .Where(x => x.Start <= release)
                .Select(x => (Row: x, Domain: DomainCode(categoryCodeById, x.CategoryId)))
                .Where(x => x.Domain is not null && Allowed(x.Domain) && memberLabelByKey.ContainsKey(x.Domain + ":" + x.Row.Code))
                .ToList();
            var expected = candidates.Count == 0
                ? []
                : candidates.GroupBy(x => x.Row.Start).OrderByDescending(g => g.Key).First()
                    .Select(x => x.Domain + ":" + x.Row.Code).Distinct(StringComparer.Ordinal).ToList();

            cases.Add(new Case(hierarchyCode, hierarchyDomain, itemId, naturalKey, expected));
        }

        return cases;
    }

    private static Dictionary<string, string> ReadMemberLabels(string sqlitePath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT d.DomainCode || ':' || m.MemberCode, COALESCE(m.MemberLabel, '') FROM mMember m JOIN mDomain d ON d.DomainID = m.DomainID";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }
}
