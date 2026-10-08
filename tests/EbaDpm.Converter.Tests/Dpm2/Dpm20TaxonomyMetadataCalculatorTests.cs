using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Mapping.Dpm20;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Synthetic tests of <see cref="Dpm20TaxonomyMetadataCalculator.Compute"/> (issue #11): the
/// derivation of <c>TaxonomyLabel</c>, <c>Version</c>, <c>PublicationDate</c>, <c>FromDate</c> and
/// <c>ToDate</c>. No data files are needed. Every expectation is written by hand from the rule in
/// <c>docs/mapping-dpm2.md</c>, with release IDs shaped like the real ones (5 and 1010000003).
/// </summary>
public sealed class Dpm20TaxonomyMetadataCalculatorTests
{
    private const int Fw = 1;

    // Release IDs: contiguous small ones, then the non-contiguous 1010000003 style.
    private const int R35 = 1;
    private const int R40 = 2;
    private const int R42 = 5;
    private const int R421 = 1010000003;
    private const int R43 = 1010000006;

    private static readonly Dpm20ReleaseRow[] Releases =
    [
        new(R35, "3.5", "2024-07-11", "released", null, false),
        new(R40, "4.0", "2024-12-19", "released", null, false),
        new(R42, "4.2", "2025-10-31", "released", null, false),
        new(R421, "4.2.1", "2026-02-15", "released", null, false),
        new(R43, "4.3", "2026-06-28", "released", null, false),
    ];

    private static readonly Dpm20FrameworkRow[] Frameworks = [new(Fw, "ALPHA", "Alpha Reporting")];

    private static int _nextVid;

    private static Dpm20ModuleVersionHistoryRow Mv(
        int start, int? end, string? version, string? from, int framework = Fw)
        => new(++_nextVid, 100 + _nextVid, framework, start, end, version, from);

    private static AccessTaxonomyRow Tax(int id, string releaseCode, int? framework = Fw)
        => new(id, framework, $"alpha {releaseCode}", null, "alpha", null, null, releaseCode, null);

    private static Dpm20TaxonomyMetadata Single(
        IReadOnlyList<Dpm20ModuleVersionHistoryRow> history, string releaseCode, int cutoff, IReadOnlyList<Dpm20ReleaseRow>? releases = null)
    {
        var result = Dpm20TaxonomyMetadataCalculator.Compute(releases ?? Releases, Frameworks, history, [Tax(1, releaseCode)], cutoff);
        return Assert.Single(result.ByTaxonomyId).Value;
    }

    [Fact]
    public void RPrimeEqualsR_UsesTheReleaseOwnModuleVersions()
    {
        var m = Single([Mv(R42, null, "4.1.0", "2026-03-31")], "4.2", R43);

        Assert.Equal(new Dpm20TaxonomyMetadata("Alpha Reporting 4.1.0 (DPM 4.2)", "4.2", "2025-10-31", "2026-03-31", "9999-12-31"), m);
    }

    [Fact]
    public void RPrimeDiffersFromR_VersionAndPublicationDateComeFromR_RestFromRPrime()
    {
        // No module version starts in 4.3: R' = 4.2.
        var history = new[] { Mv(R42, null, "4.1.0", "2026-03-31") };

        var m = Single(history, "4.3", R43);

        Assert.Equal("Alpha Reporting 4.1.0 (DPM 4.3)", m.TaxonomyLabel);
        Assert.Equal("4.3", m.Version);
        Assert.Equal("2026-06-28", m.PublicationDate);
        Assert.Equal("2026-03-31", m.FromDate);
        Assert.Equal("9999-12-31", m.ToDate);
    }

    [Fact]
    public void RPrime_RequiresTheModuleVersionToBeCurrentAtR_EndReleaseIsExclusive()
    {
        // A (start 4.0, end 4.2) is NOT current at 4.2 (End exclusive); B (start 3.5, open) is.
        var history = new[]
        {
            Mv(R40, R42, "7.0.0", "2024-12-31"),
            Mv(R35, null, "3.0.0", "2024-01-31"),
        };

        var m = Single(history, "4.2", R43);

        // R' = 3.5, not 4.0.
        Assert.Equal("Alpha Reporting 3.0.0 (DPM 4.2)", m.TaxonomyLabel);
        Assert.Equal("2024-01-31", m.FromDate);
    }

    [Fact]
    public void Version_IsComparedNumerically_TenBeatsNine()
    {
        var history = new[]
        {
            Mv(R42, null, "9.0.0", "2026-03-31"),
            Mv(R42, null, "10.0.0", "2026-03-31"),
            Mv(R42, null, "9.9.9", "2026-03-31"),
        };

        Assert.Equal("Alpha Reporting 10.0.0 (DPM 4.2)", Single(history, "4.2", R43).TaxonomyLabel);
    }

    [Fact]
    public void FromDate_IsTheLowestOfTheModuleVersionsStartingInRPrime()
    {
        var history = new[]
        {
            Mv(R42, null, "1.0.0", "2026-03-31"),
            Mv(R42, null, "1.1.0", "2025-12-31"),
            Mv(R42, null, "1.0.1", "2026-01-31"),
        };

        Assert.Equal("2025-12-31", Single(history, "4.2", R43).FromDate);
    }

    [Fact]
    public void ToDate_SkipsSuccessorsWithEarlierOrEqualFromDate_AndClosesAtTheFirstStrictlyLater()
    {
        // COREP-shaped: 3.5 (From 2025-03-31) -> 4.0 (From 2024-12-31, EARLIER) -> 4.2 (From 2026-03-31).
        // Then 4.2.1 starts with the SAME From as 4.2 (EQUAL), and 4.3 is strictly later.
        var history = new[]
        {
            Mv(R35, R40, "3.2.0", "2025-03-31"),
            Mv(R40, null, "4.0.0", "2024-12-31"),
            Mv(R42, null, "4.1.0", "2026-03-31"),
            Mv(R421, null, "4.1.1", "2026-03-31"),
            Mv(R43, null, "4.2.0", "2026-09-30"),
        };
        var taxonomies = new[] { Tax(1, "3.5"), Tax(2, "4.0"), Tax(3, "4.2"), Tax(4, "4.2.1"), Tax(5, "4.3") };

        var r = Dpm20TaxonomyMetadataCalculator.Compute(Releases, Frameworks, history, taxonomies, R43).ByTaxonomyId;

        // 3.5: skips 4.0 (earlier), closes at 4.2 (2026-03-31 - 1 day).
        Assert.Equal("2025-03-31", r[1].FromDate);
        Assert.Equal("2026-03-30", r[1].ToDate);
        // 4.0: closes at 4.2.
        Assert.Equal("2026-03-30", r[2].ToDate);
        // 4.2: skips 4.2.1 (equal), closes at 4.3 (2026-09-30 - 1 day).
        Assert.Equal("2026-09-29", r[3].ToDate);
        // 4.2.1 (R' = 4.2.1): closes at 4.3.
        Assert.Equal("2026-09-29", r[4].ToDate);
        // 4.3 is the last publication: open-ended.
        Assert.Equal(Dpm20TaxonomyMetadataCalculator.OpenEndedToDate, r[5].ToDate);
        Assert.Equal("9999-12-31", Dpm20TaxonomyMetadataCalculator.OpenEndedToDate);
    }

    [Theory]
    [InlineData("2025-03-01", "2025-02-28")] // month boundary
    [InlineData("2024-03-01", "2024-02-29")] // leap day
    [InlineData("2026-01-01", "2025-12-31")] // year boundary
    [InlineData("2026-03-31", "2026-03-30")]
    public void ToDate_IsTheDayBeforeTheSuccessorFromDate(string successorFrom, string expectedTo)
    {
        var history = new[]
        {
            Mv(R40, null, "1.0.0", "2020-01-01"),
            Mv(R42, null, "2.0.0", successorFrom),
        };

        Assert.Equal(expectedTo, Single(history, "4.0", R43).ToDate);
    }

    [Fact]
    public void ToDate_LastPublication_IsOpenEnded()
    {
        var m = Single([Mv(R42, null, "1.0.0", "2026-03-31")], "4.2", R43);

        Assert.Equal(Dpm20TaxonomyMetadataCalculator.OpenEndedToDate, m.ToDate);
    }

    [Fact]
    public void PublicationAfterTheCutoff_DoesNotInfluenceToDate()
    {
        var history = new[]
        {
            Mv(R42, null, "1.0.0", "2026-03-31"),
            Mv(R43, null, "2.0.0", "2027-03-31"), // strictly later, but after the cutoff
        };

        Assert.Equal("9999-12-31", Single(history, "4.2", R421).ToDate);

        // Positive control: with the cutoff moved to 4.3 the same row DOES close the taxonomy.
        Assert.Equal("2027-03-30", Single(history, "4.2", R43).ToDate);
    }

    [Fact]
    public void PublicationAfterTheCutoff_DoesNotInfluenceLabelEither()
    {
        // R' for 4.2.1 must not be a release beyond the cutoff.
        var history = new[]
        {
            Mv(R42, null, "1.0.0", "2026-03-31"),
            Mv(R43, null, "9.0.0", "2027-03-31"),
        };

        var m = Single(history, "4.2.1", R421);

        Assert.Equal("Alpha Reporting 1.0.0 (DPM 4.2.1)", m.TaxonomyLabel);
        Assert.Equal("2026-03-31", m.FromDate);
    }

    [Fact]
    public void ReleaseOrder_FollowsReleaseId_NotTheCodeText()
    {
        // ID 5 has code "4.9", ID 1010000003 has code "4.10": ordered by code TEXT, "4.10" would
        // come first; only the ID gives 5 < 1010000003.
        Dpm20ReleaseRow[] releases =
        [
            new(5, "4.9", "2025-10-31", "released", null, false),
            new(1010000003, "4.10", "2026-02-15", "released", null, false),
        ];
        var history = new[]
        {
            Mv(5, null, "1.0.0", "2026-01-01"),
            Mv(1010000003, null, "2.0.0", "2026-06-01"),
        };

        var first = Single(history, "4.9", 1010000003, releases);
        var second = Single(history, "4.10", 1010000003, releases);

        Assert.Equal("2026-05-31", first.ToDate);   // closed by the later-ID release
        Assert.Equal("9999-12-31", second.ToDate);
        Assert.Equal("Alpha Reporting 1.0.0 (DPM 4.9)", first.TaxonomyLabel);
        Assert.Equal("Alpha Reporting 2.0.0 (DPM 4.10)", second.TaxonomyLabel);
    }

    [Fact]
    public void OtherFrameworksModuleVersions_DoNotInfluenceTheTaxonomy()
    {
        Dpm20FrameworkRow[] frameworks = [new(Fw, "ALPHA", "Alpha Reporting"), new(2, "BETA", "Beta")];
        var history = new[]
        {
            Mv(R42, null, "1.0.0", "2026-03-31"),
            Mv(R43, null, "9.0.0", "2030-01-01", framework: 2),
        };

        var r = Dpm20TaxonomyMetadataCalculator.Compute(Releases, frameworks, history, [Tax(1, "4.2")], R43);

        Assert.Equal("9999-12-31", r.ByTaxonomyId[1].ToDate);
        Assert.Equal("Alpha Reporting 1.0.0 (DPM 4.2)", r.ByTaxonomyId[1].TaxonomyLabel);
    }

    // ------------------------------------------------------------------
    // Edge cases decided for issue #11
    // ------------------------------------------------------------------

    [Fact]
    public void UnusableRows_AreSkipped_WhenAnotherRowOfTheReleaseIsUsable()
    {
        var history = new[]
        {
            Mv(R42, null, null, null),
            Mv(R42, null, "not-a-version", "not-a-date"),
            Mv(R42, null, "", "   "),
            Mv(R42, null, "2.0.0", "2026-03-31"),
        };

        var result = Dpm20TaxonomyMetadataCalculator.Compute(Releases, Frameworks, history, [Tax(1, "4.2")], R43);
        var m = result.ByTaxonomyId[1];

        Assert.Equal(new Dpm20TaxonomyMetadata("Alpha Reporting 2.0.0 (DPM 4.2)", "4.2", "2025-10-31", "2026-03-31", "9999-12-31"), m);
        Assert.Empty(result.UnresolvedCases);
    }

    [Fact]
    public void NoUsableVersion_LeavesLabelNull_AndReportsIt_ButFromDateSurvives()
    {
        var history = new[] { Mv(R42, null, null, "2026-03-31"), Mv(R42, null, "garbage", "2026-04-30") };

        var result = Dpm20TaxonomyMetadataCalculator.Compute(Releases, Frameworks, history, [Tax(1, "4.2")], R43);
        var m = result.ByTaxonomyId[1];

        Assert.Null(m.TaxonomyLabel);
        Assert.Equal("4.2", m.Version);
        Assert.Equal("2025-10-31", m.PublicationDate);
        Assert.Equal("2026-03-31", m.FromDate);
        Assert.Equal("9999-12-31", m.ToDate);
        var line = Assert.Single(result.UnresolvedCases);
        Assert.Contains("alpha 4.2", line, StringComparison.Ordinal);
    }

    [Fact]
    public void NoUsableFromDate_LeavesFromAndToNull_AndReportsIt_ButLabelSurvives()
    {
        var history = new[] { Mv(R42, null, "1.0.0", null), Mv(R42, null, "1.1.0", "31/03/2026") };

        var result = Dpm20TaxonomyMetadataCalculator.Compute(Releases, Frameworks, history, [Tax(1, "4.2")], R43);
        var m = result.ByTaxonomyId[1];

        Assert.Equal("Alpha Reporting 1.1.0 (DPM 4.2)", m.TaxonomyLabel);
        Assert.Null(m.FromDate);
        Assert.Null(m.ToDate);
        var line = Assert.Single(result.UnresolvedCases);
        Assert.Contains("alpha 4.2", line, StringComparison.Ordinal);
    }

    [Fact]
    public void RPrimeNotFound_LabelFromToNull_ReportedVersionAndPublicationDateFromR()
    {
        // The only module version starts in 4.2; the taxonomy is for 4.0 (nothing starts at or before it).
        var history = new[] { Mv(R42, null, "1.0.0", "2026-03-31") };

        var result = Dpm20TaxonomyMetadataCalculator.Compute(Releases, Frameworks, history, [Tax(1, "4.0")], R43);
        var m = result.ByTaxonomyId[1];

        Assert.Null(m.TaxonomyLabel);
        Assert.Null(m.FromDate);
        Assert.Null(m.ToDate);
        Assert.Equal("4.0", m.Version);
        Assert.Equal("2024-12-19", m.PublicationDate);
        var line = Assert.Single(result.UnresolvedCases);
        Assert.Contains("alpha 4.0", line, StringComparison.Ordinal);
    }

    [Fact]
    public void RPrimeNotFound_WhenTheOnlyModuleVersionHasEndedBeforeR()
    {
        var history = new[] { Mv(R35, R40, "1.0.0", "2024-01-31") };

        var result = Dpm20TaxonomyMetadataCalculator.Compute(Releases, Frameworks, history, [Tax(1, "4.2")], R43);

        Assert.Null(result.ByTaxonomyId[1].TaxonomyLabel);
        Assert.Equal("4.2", result.ByTaxonomyId[1].Version);
        Assert.Single(result.UnresolvedCases);
    }

    [Fact]
    public void RPrime_IsTheGreatestStartReleaseIdAmongSeveralCurrentRows_WhateverTheRowOrder()
    {
        // At R = 4.2.1 three rows are current (open-ended or ending after R), listed out of order.
        // R' must be the greatest start (4.2), not the first listed (4.2.1 is not a start here) nor the lowest.
        var history = new[]
        {
            Mv(R42, null, "2.0.0", "2026-02-28"),
            Mv(R35, null, "1.0.0", "2024-01-31"),
            Mv(R40, R43, "1.5.0", "2025-01-31"),
        };

        var m = Single(history, "4.2.1", R421);

        Assert.Equal("Alpha Reporting 2.0.0 (DPM 4.2.1)", m.TaxonomyLabel);
        Assert.Equal("2026-02-28", m.FromDate);
    }

    [Fact]
    public void RPrimeNotFound_WhenTheFrameworkHasNoHistoryAtAll()
    {
        // History exists only for another framework: the taxonomy's framework has no rows.
        Dpm20FrameworkRow[] frameworks = [new(Fw, "ALPHA", "Alpha Reporting"), new(2, "BETA", "Beta")];
        var history = new[] { Mv(R42, null, "1.0.0", "2026-03-31", framework: 2) };

        var result = Dpm20TaxonomyMetadataCalculator.Compute(Releases, frameworks, history, [Tax(1, "4.2")], R43);
        var m = result.ByTaxonomyId[1];

        Assert.Null(m.TaxonomyLabel);
        Assert.Null(m.FromDate);
        Assert.Null(m.ToDate);
        Assert.Equal("4.2", m.Version);
        Assert.Single(result.UnresolvedCases);
    }
}
