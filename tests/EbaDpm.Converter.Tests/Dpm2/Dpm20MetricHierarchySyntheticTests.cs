using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Mapping.Dpm20;
using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Synthetic tests of the DPM 2.0 <c>mMetric.ReferencedHierarchyID</c> / <c>ReferencedDomainID</c> rule
/// (docs/mapping-dpm2.md, section "mMetric", issue 61), through <see cref="InMemoryDictionarySource"/>:
/// no Access, no data files. The candidates (non-key headers of the current table versions, then the
/// history only when there is no current one) are what the reader hands over; the loader decides.
/// The "key headers are ignored" half of the rule lives in the Access query, so it is asserted on
/// real data (<c>Dpm20OrdinateMetricRestrictionRealDataTests</c>, <c>qNCO</c>).
/// </summary>
public sealed class Dpm20MetricHierarchySyntheticTests : IDisposable
{
    private const int Cutoff = 5;
    private const int CatQco = 10;
    private const int CatQab = 11;
    private const int Sub1 = 201;
    private const int Sub2 = 202;

    private readonly string _tempDirectory;

    public Dpm20MetricHierarchySyntheticTests()
    {
        _tempDirectory = Path.Join(Path.GetTempPath(), $"EbaDpm.MetricHier_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort.
        }
    }

    private static InMemoryDictionarySource BuildSource()
    {
        var source = new InMemoryDictionarySource { CutoffReleaseId = Cutoff };
        source.DataTypes.AddRange([new(1, "e"), new(2, "i")]);
        source.Categories.AddRange(
        [
            new(1, "_PR", "Properties", null, true),
            new(2, "_TE", "Templates", null, true),
            new(3, "_NA", "Not applicable", null, false),
            new(CatQco, "QCO", "Domain QCO", null, true),
            new(CatQab, "QAB", "Domain QAB", null, true),
        ]);
        source.SubCategories.AddRange([new(Sub1, CatQco, "H1", "Hierarchy 1"), new(Sub2, CatQco, "H2", "Hierarchy 2")]);
        return source;
    }

    /// <summary>Adds an enumerated metric <paramref name="code"/> (property id, 'e', in domain QCO from release 1).</summary>
    private static void AddMetric(InMemoryDictionarySource source, int propertyId, string code, int dataTypeId = 1)
    {
        source.Properties.Add(new(propertyId, dataTypeId));
        source.ItemCategories.Add(new(propertyId, 1, code, false, 1, null));
        source.NonKeyHeaderPropertyIds.Add(propertyId);
        source.PropertyCategories.Add(new(propertyId, CatQco, 1, null));
    }

    private static void Candidate(InMemoryDictionarySource source, int propertyId, int subCategoryId, bool current)
        => source.MetricSubCategoryCandidates.Add(new(propertyId, subCategoryId, current));

    private sealed record MetricRow(string? Hierarchy, string? Domain, bool StartingIncluded);

    private (Dictionary<string, MetricRow> Rows, Dpm20DictionaryLoader.Diagnostics Diagnostics) Run(InMemoryDictionarySource source)
    {
        var path = Path.Join(_tempDirectory, $"{Guid.NewGuid():N}.db");
        SchemaCreator.Create(path, overwrite: false);
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        Dpm20DictionaryLoader.Load(source, connection, out var diagnostics);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT m.MemberCode, h.HierarchyCode, d.DomainCode, mt.IsStartingMemberIncluded
            FROM mMetric mt
            JOIN mMember m ON mt.CorrespondingMemberID = m.MemberID
            LEFT JOIN mHierarchy h ON mt.ReferencedHierarchyID = h.HierarchyID
            LEFT JOIN mDomain d ON mt.ReferencedDomainID = d.DomainID
            """;
        var rows = new Dictionary<string, MetricRow>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows[reader.GetString(0)] = new MetricRow(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3) == 1);
        }

        return (rows, diagnostics);
    }

    [Fact]
    public void SingleCurrentHierarchy_IsTheMetricsHierarchyAndDomain()
    {
        var source = BuildSource();
        AddMetric(source, 101, "ei101");
        Candidate(source, 101, Sub1, current: true);

        var (rows, diagnostics) = Run(source);

        Assert.Equal(new MetricRow("H1", "QCO", true), rows["ei101"]);
        Assert.Empty(diagnostics.MetricsWithSeveralHierarchies);
    }

    [Fact]
    public void SeveralCurrentHierarchies_GiveNullHierarchy_DomainFromPropertyCategory_AndAreListed()
    {
        var source = BuildSource();
        AddMetric(source, 102, "ei102");
        Candidate(source, 102, Sub1, current: true);
        Candidate(source, 102, Sub2, current: true);

        var (rows, diagnostics) = Run(source);

        Assert.Equal(new MetricRow(null, "QCO", false), rows["ei102"]);
        Assert.Equal(["ei102: H1, H2"], diagnostics.MetricsWithSeveralHierarchies);
    }

    [Fact]
    public void NoCurrentUse_SingleHistoricalHierarchy_IsKept()
    {
        var source = BuildSource();
        AddMetric(source, 103, "ei103");
        Candidate(source, 103, Sub2, current: false);

        var (rows, diagnostics) = Run(source);

        Assert.Equal(new MetricRow("H2", "QCO", true), rows["ei103"]);
        Assert.Empty(diagnostics.MetricsWithSeveralHierarchies);
    }

    [Fact]
    public void NoCurrentUse_SeveralHistoricalHierarchies_GiveNullHierarchy_AndAreListed()
    {
        var source = BuildSource();
        AddMetric(source, 104, "ei104");
        Candidate(source, 104, Sub1, current: false);
        Candidate(source, 104, Sub2, current: false);

        var (rows, diagnostics) = Run(source);

        Assert.Equal(new MetricRow(null, "QCO", false), rows["ei104"]);
        Assert.Equal(["ei104: H1, H2"], diagnostics.MetricsWithSeveralHierarchies);
    }

    [Fact]
    public void SeveralCurrentHierarchies_NeverFallBackToTheHistory()
    {
        var source = BuildSource();
        AddMetric(source, 105, "ei105");
        Candidate(source, 105, Sub1, current: true);
        Candidate(source, 105, Sub2, current: true);
        // A single historical hierarchy that a faulty fallback could pick.
        Candidate(source, 105, Sub1, current: false);

        var (rows, _) = Run(source);

        Assert.Null(rows["ei105"].Hierarchy);
    }

    [Fact]
    public void SingleCurrentHierarchy_IgnoresADifferentHistoricalOne()
    {
        var source = BuildSource();
        AddMetric(source, 106, "ei106");
        Candidate(source, 106, Sub1, current: true);
        Candidate(source, 106, Sub2, current: false);

        var (rows, diagnostics) = Run(source);

        Assert.Equal("H1", rows["ei106"].Hierarchy);
        Assert.Empty(diagnostics.MetricsWithSeveralHierarchies);
    }

    [Fact]
    public void NoCandidateAtAll_EnumeratedMetric_HasNullHierarchyButItsPropertyCategoryDomain()
    {
        var source = BuildSource();
        AddMetric(source, 107, "ei107");

        var (rows, diagnostics) = Run(source);

        Assert.Equal(new MetricRow(null, "QCO", false), rows["ei107"]);
        Assert.Empty(diagnostics.MetricsWithSeveralHierarchies);
    }

    [Fact]
    public void SeveralHierarchies_NotEnumerated_HasNoDomain()
    {
        var source = BuildSource();
        AddMetric(source, 108, "mi108", dataTypeId: 2); // 'i' -> Integer
        Candidate(source, 108, Sub1, current: true);
        Candidate(source, 108, Sub2, current: true);

        var (rows, _) = Run(source);

        Assert.Equal(new MetricRow(null, null, false), rows["mi108"]);
    }

    [Fact]
    public void Domain_IsThatOfThePropertyCategoryRowInForceAtTheCutoff()
    {
        var source = BuildSource();
        AddMetric(source, 109, "ei109");
        source.PropertyCategories.RemoveAll(pc => pc.PropertyId == 109);
        // QCO ends at release 3 (exclusive), QAB starts at 3: the cutoff (5) is in QAB's window.
        source.PropertyCategories.Add(new(109, CatQco, 1, 3));
        source.PropertyCategories.Add(new(109, CatQab, 3, null));

        var (rows, _) = Run(source);

        Assert.Equal(new MetricRow(null, "QAB", false), rows["ei109"]);
    }

    [Fact]
    public void Domain_IsNullWhenTheCategoriesInForceAreAmbiguous()
    {
        var source = BuildSource();
        AddMetric(source, 110, "ei110");
        source.PropertyCategories.Add(new(110, CatQab, 1, null)); // two categories in force: nothing is chosen

        var (rows, _) = Run(source);

        Assert.Equal(new MetricRow(null, null, false), rows["ei110"]);
    }

    /// <summary>Positive control: the assertions above can fail, i.e. the metrics really differ per scenario.</summary>
    [Fact]
    public void PositiveControl_TheScenariosProduceDifferentOutcomes()
    {
        var source = BuildSource();
        AddMetric(source, 101, "ei101");
        Candidate(source, 101, Sub1, current: true);
        AddMetric(source, 102, "ei102");
        Candidate(source, 102, Sub1, current: true);
        Candidate(source, 102, Sub2, current: true);

        var (rows, _) = Run(source);

        Assert.NotEqual(rows["ei101"].Hierarchy, rows["ei102"].Hierarchy);
    }
}
