namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// Without <c>--dictionary</c>, what happens to the 251 "(dimension) without domain" cells (151
/// distinct codes)?
///
/// Measured: they are silently lost INSIDE a wrong category. The 251 cells are recorded as
/// <c>LayoutValue</c> with <c>DomainCode = 'MET'</c> (as if they were the column's metric), mixed
/// indistinguishably with the 1,055 real MET values. The only warning is ONE global CLI line ("open
/// axis key dimensions may be undetected"); there is no per-cell or per-row mark that would allow,
/// by querying only the resulting SQLite repository, to later tell which of those 1,206 "MET" rows
/// are real and which are mislabelled dimensions.
/// </summary>
[Collection("LayoutsRealData")]
public sealed class LayoutNoDictionaryFallbackTests(LayoutRepositoryFixture fixture)
{
    [DataFact]
    public void WithoutDictionary_The251AmbiguousCells_AreSilentlyMisfiledAsMetricValues()
    {
        var withDict = fixture.Repository42WithDictionary;
        var noDict = fixture.Repository42NoDictionary;

        var metMembersWithDict = QueryDistinctMetMembers(withDict);
        var metMembersNoDict = QueryDistinctMetMembers(noDict);

        // Without a dictionary 151 extra "MET" codes appear -- exactly those that, WITH a
        // dictionary, are correctly resolved as dimensions.
        var onlyWithoutDictionary = metMembersNoDict.Except(metMembersWithDict).ToHashSet();
        Assert.Equal(151, onlyWithoutDictionary.Count);

        using var cmd = noDict.CreateCommand();
        var placeholders = string.Join(",", onlyWithoutDictionary.Select((_, i) => $"$p{i}"));
        cmd.CommandText = $"SELECT COUNT(*) FROM LayoutValue WHERE DomainCode = 'MET' AND MemberCode IN ({placeholders})";
        var i = 0;
        foreach (var code in onlyWithoutDictionary)
        {
            cmd.Parameters.AddWithValue($"$p{i}", code);
            i++;
        }

        Assert.Equal(251L, Convert.ToInt64(cmd.ExecuteScalar()));

        // And there is NO per-cell mark that distinguishes these 251 rows from the rest of the real
        // MET rows: the repository schema (LayoutValue) has no column for it.
        using var schemaCheck = noDict.CreateCommand();
        schemaCheck.CommandText = "PRAGMA table_info(LayoutValue)";
        using var reader = schemaCheck.ExecuteReader();
        var columnNames = new List<string>();
        while (reader.Read())
        {
            columnNames.Add(reader.GetString(1));
        }

        Assert.DoesNotContain("Confidence", columnNames);
        Assert.DoesNotContain("Ambiguous", columnNames);
        Assert.DoesNotContain("ResolvedWithoutDictionary", columnNames);
    }

    private static HashSet<string> QueryDistinctMetMembers(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT MemberCode FROM LayoutValue WHERE DomainCode = 'MET'";
        using var reader = cmd.ExecuteReader();
        var set = new HashSet<string>();
        while (reader.Read())
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }
}
