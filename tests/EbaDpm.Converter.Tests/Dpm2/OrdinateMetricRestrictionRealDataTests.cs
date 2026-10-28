using System.Globalization;
using EbaDpm.Converter.Tests.All;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.PlaneC;
using EbaDpm.Converter.Tests.Schema;
using EbaDpm.Converter.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Shared queries of the real-data tests of <c>mOrdinateMetricRestriction</c> and of the DPM 2.0
/// <c>mMetric</c> hierarchy rule (issue 61). Everything is by business key (metric code, hierarchy
/// code, table code, ordinate code), never by surrogate ID.
/// </summary>
internal static class OmrRealData
{
    public const string RestrictionRows =
        """
        SELECT t.TableCode, ao.OrdinateCode, mm.MemberCode, h.HierarchyCode, d.DomainCode,
               sm.MemberCode, r.IsStartingMemberIncluded
        FROM mOrdinateMetricRestriction r
        JOIN mHierarchy h ON h.HierarchyID = r.HierarchyID
        JOIN mDomain d ON d.DomainID = h.DomainID
        JOIN mAxisOrdinate ao ON ao.OrdinateID = r.OrdinateID
        JOIN mOrdinateCategorisation oc ON oc.OrdinateID = r.OrdinateID
        JOIN mDimension dim ON dim.DimensionID = oc.DimensionID AND dim.DimensionXBRLCode = 'MET'
        JOIN mMember mm ON mm.MemberID = oc.MemberID
        LEFT JOIN mMember sm ON sm.MemberID = r.HierarchyStartingMemberID
        JOIN mTableAxis ta ON ta.AxisID = ao.AxisID
        JOIN mTable t ON t.TableID = ta.TableID
        """;

    public static SqliteConnection OpenReadOnly(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    public static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static List<string?[]> Rows(SqliteConnection c, string sql, int columns)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string?[]>();
        while (reader.Read())
        {
            var row = new string?[columns];
            for (var i = 0; i < columns; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// "No invented data": every item of <paramref name="produced"/> must be in <paramref name="source"/>.
    /// Returns the items that are not (an empty list is the pass).
    /// </summary>
    public static List<string> NotInSource(IEnumerable<string> produced, ISet<string> source)
        => produced.Where(p => !source.Contains(p)).Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>Plane A checks of the extension table on a real output: all passed, each with the row count as universe.</summary>
    public static void AssertPlaneAOfTheExtension(string generatedPath, long expectedRows)
    {
        var result = ValidatorRunCache.Run(generatedPath, referencePath: null);
        foreach (var id in new[] { "A-OMR-01", "A-OMR-02", "A-OMR-03", "A-OMR-04", "I-REF-17.1", "I-REF-17.2", "I-REF-17.3" })
        {
            var check = Assert.Single(result.Report.Checks, c => c.Id == id);
            Assert.True(
                check.Status == "pass" && check.Failed == 0 && check.Examined == expectedRows,
                $"{id}: status={check.Status}, examined={check.Examined} (expected {expectedRows}), failed={check.Failed}; " +
                $"samples=[{string.Join(" || ", check.Samples.Select(s => s.BusinessKey))}]");
        }

        var vac = Assert.Single(result.Report.Checks, c => c.Id == "A-VAC-03");
        Assert.Equal("pass", vac.Status);
    }

    /// <summary>The non-key (property code, hierarchy code) pairs that a DPM 2.0 Access database declares, any table version.</summary>
    public static HashSet<string> Dpm20SourcePairs(string accdb)
    {
        var prCategoryId = AccessCensus.Scalar(accdb, "SELECT [CategoryID] FROM [Category] WHERE [Code] = '_PR'");
        var propertyCodes = new Dictionary<int, List<string>>();
        foreach (var row in AccessCensus.Rows(accdb, $"SELECT [ItemID], [Code] FROM [ItemCategory] WHERE [CategoryID] = {prCategoryId}"))
        {
            var id = Convert.ToInt32(row[0], CultureInfo.InvariantCulture);
            if (!propertyCodes.TryGetValue(id, out var list))
            {
                list = [];
                propertyCodes[id] = list;
            }

            list.Add(Convert.ToString(row[1], CultureInfo.InvariantCulture)!);
        }

        var subCategoryIdByVid = AccessCensus.Rows(accdb, "SELECT [SubCategoryVID], [SubCategoryID] FROM [SubCategoryVersion]")
            .ToDictionary(r => Convert.ToInt32(r[0], CultureInfo.InvariantCulture), r => Convert.ToInt32(r[1], CultureInfo.InvariantCulture));
        var subCategoryCode = AccessCensus.Rows(accdb, "SELECT [SubCategoryID], [Code] FROM [SubCategory]")
            .ToDictionary(r => Convert.ToInt32(r[0], CultureInfo.InvariantCulture), r => Convert.ToString(r[1], CultureInfo.InvariantCulture)!);

        var pairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in AccessCensus.Rows(
                     accdb,
                     "SELECT hv.[PropertyID], hv.[SubCategoryVID] FROM [HeaderVersion] hv INNER JOIN [Header] h ON h.[HeaderID] = hv.[HeaderID] "
                     + "WHERE h.[IsKey] = 0 AND hv.[PropertyID] IS NOT NULL AND hv.[SubCategoryVID] IS NOT NULL"))
        {
            var propertyId = Convert.ToInt32(row[0], CultureInfo.InvariantCulture);
            if (!propertyCodes.TryGetValue(propertyId, out var codes)
                || !subCategoryIdByVid.TryGetValue(Convert.ToInt32(row[1], CultureInfo.InvariantCulture), out var scId)
                || !subCategoryCode.TryGetValue(scId, out var hierarchyCode))
            {
                continue;
            }

            foreach (var code in codes)
            {
                pairs.Add($"{code}|{hierarchyCode}");
            }
        }

        return pairs;
    }

    /// <summary>Every (metric, hierarchy) pair of the output must be declared by a non-key header of the source.</summary>
    public static void AssertDpm20PairsAreDeclaredBySource(string generatedPath, string accdb)
    {
        var source = Dpm20SourcePairs(accdb);
        Assert.NotEmpty(source);

        using var c = OpenReadOnly(generatedPath);
        var produced = Rows(c, OmrRealData.RestrictionRows, 7).Select(r => $"{r[2]}|{r[3]}").ToList();
        Assert.NotEmpty(produced);

        Assert.Empty(NotInSource(produced, source));

        // Positive control: the same helper does find a pair that the source does not declare.
        Assert.Equal(["qAYV|no_such_hierarchy"], NotInSource([.. produced.Take(1), "qAYV|no_such_hierarchy"], source));
    }

    public static void AssertNoStartingMember(SqliteConnection c)
    {
        Assert.Equal(0, Scalar(c, "SELECT COUNT(1) FROM mOrdinateMetricRestriction WHERE HierarchyStartingMemberID IS NOT NULL OR IsStartingMemberIncluded IS NOT NULL"));
    }
}

/// <summary>DPM 1.0 4.1 (<c>--all</c>): the restrictions of the metric categorisations.</summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class OrdinateMetricRestrictionDpm10RealDataTests(AllFixture fixture, ITestOutputHelper output)
{
    private static readonly string[] MetricsWithAnotherHierarchy =
        ["ei1494", "ei1529", "ei207", "ei219", "ei248", "ei786", "ei794", "ei834", "ei912", "ei932", "qANY", "qEBF", "qNMZ"];

    [DataFact]
    public void RowCounts_MatchThePinnedCensus_AndTheLoaderResult()
    {
        using var c = OmrRealData.OpenReadOnly(fixture.ValidatedDatabasePath);

        var rows = OmrRealData.Scalar(c, "SELECT COUNT(1) FROM mOrdinateMetricRestriction");
        ReleasePins.AssertPinned(ReleasePins.Dpm10All41, "mOrdinateMetricRestriction rows", ReleasePins.Dpm10All41OrdinateMetricRestrictionRows, rows);
        Assert.Equal(rows, fixture.AxisAndCellResult.OrdinateMetricRestrictionRows);

        ReleasePins.AssertPinned(
            ReleasePins.Dpm10All41, "rows with a starting member", ReleasePins.Dpm10All41OrdinateMetricRestrictionWithStartingMember,
            OmrRealData.Scalar(c, "SELECT COUNT(1) FROM mOrdinateMetricRestriction WHERE HierarchyStartingMemberID IS NOT NULL"));
        Assert.Equal(0, OmrRealData.Scalar(c, "SELECT COUNT(1) FROM mOrdinateMetricRestriction WHERE (HierarchyStartingMemberID IS NULL) <> (IsStartingMemberIncluded IS NULL)"));
    }

    [DataFact]
    public void HierarchiesDifferingFromTheMetric_AreThe48RowsOfTheThirteenNamedMetrics()
    {
        using var c = OmrRealData.OpenReadOnly(fixture.ValidatedDatabasePath);
        const string differing =
            """
            FROM mOrdinateMetricRestriction r
            JOIN mHierarchy rh ON rh.HierarchyID = r.HierarchyID
            JOIN mOrdinateCategorisation oc ON oc.OrdinateID = r.OrdinateID
            JOIN mDimension dim ON dim.DimensionID = oc.DimensionID AND dim.DimensionXBRLCode = 'MET'
            JOIN mMember mm ON mm.MemberID = oc.MemberID
            JOIN mMetric me ON me.CorrespondingMemberID = mm.MemberID
            JOIN mHierarchy mh ON mh.HierarchyID = me.ReferencedHierarchyID
            WHERE mh.HierarchyCode <> rh.HierarchyCode
            """;

        ReleasePins.AssertPinned(
            ReleasePins.Dpm10All41, "restrictions whose hierarchy differs from mMetric's", ReleasePins.Dpm10All41OrdinateMetricRestrictionDifferingFromMetric,
            OmrRealData.Scalar(c, "SELECT COUNT(1) " + differing));

        var metrics = OmrRealData.Rows(c, "SELECT DISTINCT mm.MemberCode " + differing, 1).Select(r => r[0]!).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(MetricsWithAnotherHierarchy.Order(StringComparer.Ordinal).ToList(), metrics);

        // The restrictions equal to the metric's own hierarchy are emitted too (no selective omission).
        var equalToMetric = OmrRealData.Scalar(
            c,
            """
            SELECT COUNT(1)
            FROM mOrdinateMetricRestriction r
            JOIN mHierarchy rh ON rh.HierarchyID = r.HierarchyID
            JOIN mOrdinateCategorisation oc ON oc.OrdinateID = r.OrdinateID
            JOIN mDimension dim ON dim.DimensionID = oc.DimensionID AND dim.DimensionXBRLCode = 'MET'
            JOIN mMetric me ON me.CorrespondingMemberID = oc.MemberID
            JOIN mHierarchy mh ON mh.HierarchyID = me.ReferencedHierarchyID
            WHERE mh.HierarchyCode = rh.HierarchyCode
            """);
        Assert.True(equalToMetric > 0, "No restriction repeats the metric's hierarchy: the 'every declared restriction is emitted' rule is not exercised.");
    }

    /// <summary>
    /// Independent of the loader: every (metric, hierarchy, starting member) triple of the output
    /// is declared by an <c>OrdinateCategorisation.RestrictionID</c> of the Access database
    /// (<c>OpenMemberRestriction</c> through <c>Hierarchy</c> and <c>Member</c>), a starting member
    /// only when <c>IgnoreMemberID</c> = 0, with its <c>MemberIncluded</c>.
    /// </summary>
    [DataFact]
    public void EveryRow_IsDeclaredByTheAccessDatabase_IncludingTheStartingMember()
    {
        RepoPaths.EnsureAccessDatabaseExists();
        var accdb = RepoPaths.AccessDatabasePath;

        var metricIds = AccessCensus.Ints(accdb, "SELECT [MetricID] FROM [Metric]").ToHashSet();
        var memberCode = AccessCensus.Rows(accdb, "SELECT [MemberID], [MemberCode] FROM [Member]")
            .ToDictionary(r => Convert.ToInt32(r[0], CultureInfo.InvariantCulture), r => Convert.ToString(r[1], CultureInfo.InvariantCulture)!);
        var hierarchyCode = AccessCensus.Rows(accdb, "SELECT [HierarchyID], [HierarchyCode] FROM [Hierarchy]")
            .ToDictionary(r => Convert.ToInt32(r[0], CultureInfo.InvariantCulture), r => Convert.ToString(r[1], CultureInfo.InvariantCulture)!);

        var source = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in AccessCensus.Rows(
                     accdb,
                     "SELECT oc.[MemberID], r.[HierarchyID], r.[MemberID], r.[IgnoreMemberID], r.[MemberIncluded] "
                     + "FROM [OrdinateCategorisation] oc INNER JOIN [OpenMemberRestriction] r ON r.[RestrictionID] = oc.[RestrictionID] "
                     + "WHERE oc.[RestrictionID] IS NOT NULL"))
        {
            var metricId = Convert.ToInt32(row[0], CultureInfo.InvariantCulture);
            if (!metricIds.Contains(metricId) || row[1] is DBNull || !memberCode.TryGetValue(metricId, out var metric))
            {
                continue;
            }

            var ignore = Convert.ToBoolean(row[3], CultureInfo.InvariantCulture);
            var start = ignore || row[2] is DBNull ? null : memberCode[Convert.ToInt32(row[2], CultureInfo.InvariantCulture)];
            var included = start is null ? "-" : Convert.ToBoolean(row[4], CultureInfo.InvariantCulture) ? "1" : "0";
            source.Add($"{metric}|{hierarchyCode[Convert.ToInt32(row[1], CultureInfo.InvariantCulture)]}|{start ?? "-"}|{included}");
        }

        Assert.NotEmpty(source);

        using var c = OmrRealData.OpenReadOnly(fixture.ValidatedDatabasePath);
        var produced = OmrRealData.Rows(c, OmrRealData.RestrictionRows, 7)
            .Select(r => $"{r[2]}|{r[3]}|{r[5] ?? "-"}|{(r[5] is null ? "-" : r[6])}")
            .ToList();
        Assert.Equal(ReleasePins.Dpm10All41OrdinateMetricRestrictionRows, produced.Count);

        Assert.Empty(OmrRealData.NotInSource(produced, source));

        // Positive control: a starting member that the source does not declare is detected.
        Assert.Single(OmrRealData.NotInSource([.. produced, "ei207|SC1|qx0000|1"], source));
        Assert.True(produced.Count(p => !p.Contains("|-|", StringComparison.Ordinal)) > 0, "No row with a starting member: that branch is not exercised.");
    }

    [DataFact]
    public void PlaneA_TheExtensionChecks_PassOverEveryRow()
        => OmrRealData.AssertPlaneAOfTheExtension(fixture.ValidatedDatabasePath, ReleasePins.Dpm10All41OrdinateMetricRestrictionRows);

    [DataFact]
    public void PlaneC_CMhr_OnTheDpm10Output_WithLayouts42_IsReported()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);
        var c01 = Assert.Single(result.Report.Checks, c => c.Id == "C-MHR-01");
        var c02 = Assert.Single(result.Report.Checks, c => c.Id == "C-MHR-02");
        output.WriteLine($"C-MHR-01: {c01.Status} examined={c01.Examined} failed={c01.Failed} {c01.SkipReason}");
        output.WriteLine($"C-MHR-02: {c02.Statement}");
        foreach (var s in c01.Samples)
        {
            output.WriteLine($"  violation {s.BusinessKey} generated={s.Generated} layout={s.Reference}");
        }

        Assert.NotEqual("fail", c01.Status);
    }
}

/// <summary>DPM 2.0 4.2.1 read with cutoff 4.2 (the <c>Dpm2Skeleton</c> collection).</summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class OrdinateMetricRestrictionDpm20Cutoff42RealDataTests(Dpm20SkeletonFixture fixture, ITestOutputHelper output)
{
    [DataFact]
    public void RowCount_MatchesThePinnedCensus_WithNoStartingMember()
    {
        using var c = OmrRealData.OpenReadOnly(fixture.ValidatedDatabasePath);
        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Cutoff42, "mOrdinateMetricRestriction rows", ReleasePins.Dpm20Cutoff42OrdinateMetricRestrictionRows,
            OmrRealData.Scalar(c, "SELECT COUNT(1) FROM mOrdinateMetricRestriction"));
        OmrRealData.AssertNoStartingMember(c);
    }

    [DataFact]
    public void EveryPair_IsDeclaredByANonKeyHeaderOfTheAccessDatabase()
        => OmrRealData.AssertDpm20PairsAreDeclaredBySource(fixture.ValidatedDatabasePath, RepoPaths.AccessDpm20DatabasePath);

    [DataFact]
    public void PlaneA_TheExtensionChecks_PassOverEveryRow()
        => OmrRealData.AssertPlaneAOfTheExtension(fixture.ValidatedDatabasePath, ReleasePins.Dpm20Cutoff42OrdinateMetricRestrictionRows);

    [DataFact]
    public void PlaneC_CMhr_WithLayouts42_PassesAndReportsItsCensus()
        => OmrPlaneCAssertions.AssertCMhr(fixture.ValidatedDatabasePath, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath, output);

    [DataFact]
    public void TheSeveralHierarchiesDiagnostic_NamesEi794_AndItIsHistoryOnly()
    {
        Assert.Contains("mMetric with several hierarchies in its non-key headers", fixture.Stdout, StringComparison.Ordinal);
        Assert.Contains("- ei794: AP29, qAP31", fixture.Stdout, StringComparison.Ordinal);

        using var c = OmrRealData.OpenReadOnly(fixture.ValidatedDatabasePath);
        var ei794 = OmrRealData.Rows(
            c,
            """
            SELECT h.HierarchyCode, d.DomainCode
            FROM mMetric me JOIN mMember mm ON mm.MemberID = me.CorrespondingMemberID
            LEFT JOIN mHierarchy h ON h.HierarchyID = me.ReferencedHierarchyID
            LEFT JOIN mDomain d ON d.DomainID = me.ReferencedDomainID
            WHERE mm.MemberCode = 'ei794'
            """, 2);
        var row = Assert.Single(ei794);
        Assert.Null(row[0]); // several hierarchies in the history: none chosen
        Assert.NotNull(row[1]); // the domain comes from PropertyCategory

        Assert.Equal(0, OmrRealData.Scalar(c, "SELECT COUNT(1) FROM mOrdinateMetricRestriction r JOIN mOrdinateCategorisation oc ON oc.OrdinateID = r.OrdinateID JOIN mMember mm ON mm.MemberID = oc.MemberID WHERE mm.MemberCode = 'ei794'"));
    }
}

/// <summary>DPM 2.0 4.2.1 with its natural cutoff (4.2.1).</summary>
[Collection("Dpm2042_1ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class OrdinateMetricRestrictionDpm20Natural421RealDataTests(Dpm2042_1ValidateFixture fixture, ITestOutputHelper output)
{
    [DataFact]
    public void Figures_MatchThePinnedCensus_AndQayvIsOnlyInCorep()
    {
        using var c = OmrRealData.OpenReadOnly(fixture.GeneratedDatabasePath);
        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Natural421, "mOrdinateMetricRestriction rows", ReleasePins.Dpm20Natural421OrdinateMetricRestrictionRows,
            OmrRealData.Scalar(c, "SELECT COUNT(1) FROM mOrdinateMetricRestriction"));
        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Natural421, "mMetric rows with a hierarchy", ReleasePins.Dpm20Natural421MetricsWithHierarchy,
            OmrRealData.Scalar(c, "SELECT COUNT(1) FROM mMetric WHERE ReferencedHierarchyID IS NOT NULL"));
        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Natural421, "mMetric rows with a domain and no hierarchy", ReleasePins.Dpm20Natural421MetricsWithDomainOnly,
            OmrRealData.Scalar(c, "SELECT COUNT(1) FROM mMetric WHERE ReferencedHierarchyID IS NULL AND ReferencedDomainID IS NOT NULL"));
        OmrRealData.AssertNoStartingMember(c);

        // 4.2.1: no property uses more than one hierarchy in the current tables: no metric has several.
        var qayv = OmrRealData.Rows(
            c, OmrRealData.RestrictionRows + " WHERE mm.MemberCode = 'qAYV' ORDER BY t.TableCode", 7).Select(r => $"{r[0]}/{r[1]}/{r[3]}").Distinct().ToList();
        Assert.Equal(["C_67.00.a/0016/new_CO4", "C_67.00.w/0016/new_CO4"], qayv);
    }

    [DataFact]
    public void EveryPair_IsDeclaredByANonKeyHeaderOfTheAccessDatabase()
        => OmrRealData.AssertDpm20PairsAreDeclaredBySource(fixture.GeneratedDatabasePath, RepoPaths.AccessDpm20DatabasePath);

    [DataFact]
    public void PlaneA_TheExtensionChecks_PassOverEveryRow()
        => OmrRealData.AssertPlaneAOfTheExtension(fixture.GeneratedDatabasePath, ReleasePins.Dpm20Natural421OrdinateMetricRestrictionRows);

    [DataFact]
    public void PlaneC_CMhr_WithLayouts42_PassesAndReportsItsCensus()
        => OmrPlaneCAssertions.AssertCMhr(fixture.GeneratedDatabasePath, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath, output);
}

/// <summary>DPM 2.0 4.3 (20260622), natural cutoff 4.3.</summary>
[Collection("Dpm2043ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class OrdinateMetricRestrictionDpm20Natural43RealDataTests(Dpm2043ValidateFixture fixture, ITestOutputHelper output)
{
    [DataFact]
    public void Figures_MatchThePinnedCensus()
    {
        using var c = OmrRealData.OpenReadOnly(fixture.GeneratedDatabasePath);
        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Natural43, "mOrdinateMetricRestriction rows", ReleasePins.Dpm20Natural43OrdinateMetricRestrictionRows,
            OmrRealData.Scalar(c, "SELECT COUNT(1) FROM mOrdinateMetricRestriction"));
        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Natural43, "mMetric rows with a hierarchy", ReleasePins.Dpm20Natural43MetricsWithHierarchy,
            OmrRealData.Scalar(c, "SELECT COUNT(1) FROM mMetric WHERE ReferencedHierarchyID IS NOT NULL"));
        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Natural43, "mMetric rows with a domain and no hierarchy", ReleasePins.Dpm20Natural43MetricsWithDomainOnly,
            OmrRealData.Scalar(c, "SELECT COUNT(1) FROM mMetric WHERE ReferencedHierarchyID IS NULL AND ReferencedDomainID IS NOT NULL"));
        OmrRealData.AssertNoStartingMember(c);
    }

    /// <summary>The worked example of issue 61: the five non-key headers that use <c>qAYV</c>.</summary>
    [DataFact]
    public void Qayv_HasThePerOrdinateHierarchyOfEachTable_AndNoHierarchyOnTheMetric()
    {
        using var c = OmrRealData.OpenReadOnly(fixture.GeneratedDatabasePath);

        var rows = OmrRealData.Rows(
            c, OmrRealData.RestrictionRows + " WHERE mm.MemberCode = 'qAYV' ORDER BY t.TableCode", 7)
            .Select(r => $"{r[0]}|{r[1]}|{r[3]}|{r[4]}").ToList();
        Assert.Equal(
            [
                "C_67.00.a|0016|new_CO4|qCO",
                "C_67.00.w|0016|new_CO4|qCO",
                "E_04.01|0020|new_CO5|qCO",
                "E_04.02|0020|new_CO5|qCO",
                "E_08.01|0050|new_CO5|qCO",
            ],
            rows);

        var metric = Assert.Single(OmrRealData.Rows(
            c,
            """
            SELECT h.HierarchyCode, d.DomainCode
            FROM mMetric me JOIN mMember mm ON mm.MemberID = me.CorrespondingMemberID
            LEFT JOIN mHierarchy h ON h.HierarchyID = me.ReferencedHierarchyID
            LEFT JOIN mDomain d ON d.DomainID = me.ReferencedDomainID
            WHERE mm.MemberCode = 'qAYV'
            """, 2));
        Assert.Null(metric[0]);
        Assert.Equal("qCO", metric[1]);
    }

    /// <summary>
    /// Key headers no longer count (issue 61, defect 1): <c>qNCO</c> is <c>new_CO5</c> (its non-key
    /// uses E_03.01/0020 and E_03.02/0020), no longer <c>new_CO1</c> (its key headers).
    /// </summary>
    [DataFact]
    public void Qnco_TakesItsHierarchyFromNonKeyHeadersOnly()
    {
        using var c = OmrRealData.OpenReadOnly(fixture.GeneratedDatabasePath);
        var metric = Assert.Single(OmrRealData.Rows(
            c,
            """
            SELECT h.HierarchyCode FROM mMetric me JOIN mMember mm ON mm.MemberID = me.CorrespondingMemberID
            LEFT JOIN mHierarchy h ON h.HierarchyID = me.ReferencedHierarchyID WHERE mm.MemberCode = 'qNCO'
            """, 1));
        Assert.Equal("new_CO5", metric[0]);

        var restrictions = OmrRealData.Rows(
            c, OmrRealData.RestrictionRows + " WHERE mm.MemberCode = 'qNCO' ORDER BY t.TableCode", 7).Select(r => $"{r[0]}/{r[1]}/{r[3]}").ToList();
        Assert.Equal(["E_03.01/0020/new_CO5", "E_03.02/0020/new_CO5"], restrictions);
    }

    /// <summary>
    /// Metrics without a single hierarchy in 4.3: NULL hierarchy, domain from PropertyCategory. The
    /// five whose current tables use several are exactly those with several distinct per-ordinate
    /// hierarchies; <c>ei794</c> has several only in the history (no restriction row).
    /// </summary>
    [DataFact]
    public void MetricsWithSeveralHierarchies_HaveNullHierarchy_AndTheirRestrictionsDiffer()
    {
        using var c = OmrRealData.OpenReadOnly(fixture.GeneratedDatabasePath);

        var severalInCurrentTables = OmrRealData.Rows(
            c,
            """
            SELECT mm.MemberCode
            FROM mOrdinateMetricRestriction r
            JOIN mOrdinateCategorisation oc ON oc.OrdinateID = r.OrdinateID
            JOIN mDimension dim ON dim.DimensionID = oc.DimensionID AND dim.DimensionXBRLCode = 'MET'
            JOIN mMember mm ON mm.MemberID = oc.MemberID
            GROUP BY mm.MemberCode HAVING COUNT(DISTINCT r.HierarchyID) > 1
            ORDER BY mm.MemberCode
            """, 1).Select(r => r[0]!).ToList();
        Assert.Equal(
            new[] { "GXI", "ei351", "qAPH", "qAYV", "qAZC" }.Order(StringComparer.Ordinal).ToList(),
            severalInCurrentTables.Order(StringComparer.Ordinal).ToList());

        foreach (var code in new[] { "ei351", "GXI", "qAPH", "qAYV", "qAZC", "ei794" })
        {
            var metric = Assert.Single(OmrRealData.Rows(
                c,
                $"""
                SELECT h.HierarchyCode, d.DomainCode FROM mMetric me JOIN mMember mm ON mm.MemberID = me.CorrespondingMemberID
                LEFT JOIN mHierarchy h ON h.HierarchyID = me.ReferencedHierarchyID LEFT JOIN mDomain d ON d.DomainID = me.ReferencedDomainID
                WHERE mm.MemberCode = '{code}'
                """, 2));
            Assert.True(metric[0] is null, $"{code}: mMetric names the hierarchy {metric[0]} although its headers use several.");
            Assert.True(metric[1] is not null, $"{code}: no ReferencedDomainID (PropertyCategory).");
        }

        Assert.Equal(
            0,
            OmrRealData.Scalar(
                c,
                "SELECT COUNT(1) FROM mOrdinateMetricRestriction r JOIN mOrdinateCategorisation oc ON oc.OrdinateID = r.OrdinateID JOIN mMember mm ON mm.MemberID = oc.MemberID WHERE mm.MemberCode = 'ei794'"));
    }

    [DataFact]
    public void EveryPair_IsDeclaredByANonKeyHeaderOfTheAccessDatabase()
        => OmrRealData.AssertDpm20PairsAreDeclaredBySource(fixture.GeneratedDatabasePath, RepoPaths.AccessDpm20Release43DatabasePath);

    [DataFact]
    public void PlaneA_TheExtensionChecks_PassOverEveryRow()
        => OmrRealData.AssertPlaneAOfTheExtension(fixture.GeneratedDatabasePath, ReleasePins.Dpm20Natural43OrdinateMetricRestrictionRows);

    [DataFact]
    public void PlaneC_CMhr_WithLayouts43_PassesAndReportsItsCensus()
        => OmrPlaneCAssertions.AssertCMhr(fixture.GeneratedDatabasePath, PlaneCLayout43RepositoryHolder.LayoutsDatabasePath, output);

    /// <summary>
    /// Positive control of C-MHR-01 on real data: on a COPY of the real 4.3 output, the restriction of
    /// qAYV in E_04.01 column 0020 is changed to the other hierarchy of the same domain (new_CO4 instead
    /// of new_CO5, what the layout contradicts). The check, which passes on the untouched output, must
    /// then fail with exactly that unit.
    /// </summary>
    [DataFact]
    public void PlaneC_CMhr_OnACopyWithQayvOfE0401Altered_FailsWithThatUnit()
    {
        var dir = Path.Join(Path.GetTempPath(), $"EbaDpm.CMhrControl_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var copy = Path.Join(dir, "copy43.db");
            SqliteConnection.ClearAllPools();
            File.Copy(fixture.GeneratedDatabasePath, copy);

            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText =
                    """
                    UPDATE mOrdinateMetricRestriction
                    SET HierarchyID = (SELECT h.HierarchyID FROM mHierarchy h JOIN mDomain d ON d.DomainID = h.DomainID
                                       WHERE d.DomainCode = 'qCO' AND h.HierarchyCode = 'new_CO4')
                    WHERE OrdinateID IN (
                        SELECT ao.OrdinateID FROM mAxisOrdinate ao
                        JOIN mTableAxis ta ON ta.AxisID = ao.AxisID JOIN mTable t ON t.TableID = ta.TableID
                        JOIN mOrdinateCategorisation oc ON oc.OrdinateID = ao.OrdinateID
                        JOIN mMember mm ON mm.MemberID = oc.MemberID
                        WHERE t.TableCode = 'E_04.01' AND ao.OrdinateCode = '0020' AND mm.MemberCode = 'qAYV')
                    """;
                Assert.Equal(1, cmd.ExecuteNonQuery());
            }

            SqliteConnection.ClearAllPools();
            var result = ValidatorRunCache.Run(copy, referencePath: null, PlaneCLayout43RepositoryHolder.LayoutsDatabasePath);
            var c01 = Assert.Single(result.Report.Checks, c => c.Id == "C-MHR-01");

            Assert.Equal("fail", c01.Status);
            Assert.Equal(1, c01.Failed);
            var sample = Assert.Single(c01.Samples);
            Assert.StartsWith("E_04.01	4.3|", sample.BusinessKey, StringComparison.Ordinal);
            Assert.EndsWith("|0020|qAYV", sample.BusinessKey, StringComparison.Ordinal);
            Assert.Contains("new_CO4", sample.Generated, StringComparison.Ordinal);
            Assert.Equal("qCO:new_CO5", sample.Reference);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort.
            }
        }
    }

    /// <summary>The 4.2 layouts against a 4.3 output: other release labels, so the check has little or nothing to compare; it must not fail.</summary>
    [DataFact]
    public void PlaneC_CMhr_WithLayouts42_DoesNotFail()
    {
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, referencePath: null, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);
        var c01 = Assert.Single(result.Report.Checks, c => c.Id == "C-MHR-01");
        output.WriteLine($"C-MHR-01 (4.3 output, 4.2 layouts): {c01.Status} examined={c01.Examined} failed={c01.Failed} {c01.SkipReason}");
        Assert.NotEqual("fail", c01.Status);
    }
}

internal static class OmrPlaneCAssertions
{
    /// <summary>
    /// C-MHR-01 must not fail and must have compared something (a pass over an empty universe is
    /// reported as Skipped by the check itself, which would fail the first assertion); C-MHR-02 is
    /// the census, printed to the test output.
    /// </summary>
    public static void AssertCMhr(string generatedPath, string layoutsPath, ITestOutputHelper output)
    {
        var result = ValidatorRunCache.Run(generatedPath, referencePath: null, layoutsPath);
        var c01 = Assert.Single(result.Report.Checks, c => c.Id == "C-MHR-01");
        var c02 = Assert.Single(result.Report.Checks, c => c.Id == "C-MHR-02");

        output.WriteLine($"C-MHR-01: {c01.Status} examined={c01.Examined} failed={c01.Failed} {c01.SkipReason}");
        output.WriteLine($"C-MHR-02: {c02.Statement}");
        foreach (var s in c02.Samples)
        {
            output.WriteLine($"  census sample {s.BusinessKey} [{s.Generated}] layout={s.Reference}");
        }

        foreach (var s in c01.Samples)
        {
            output.WriteLine($"  violation {s.BusinessKey} generated={s.Generated} layout={s.Reference}");
        }

        Assert.True(
            c01.Status == "pass" && c01.Examined > 0 && c01.Failed == 0,
            $"C-MHR-01: status={c01.Status}, examined={c01.Examined}, failed={c01.Failed}, skip={c01.SkipReason}; " +
            $"violations=[{string.Join(" || ", c01.Samples.Select(s => $"{s.BusinessKey} generated={s.Generated} layout={s.Reference}"))}]");
        Assert.Equal("info", c02.Status);
    }
}
