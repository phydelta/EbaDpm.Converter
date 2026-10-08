using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Mapping;

namespace EbaDpm.Converter.Tests.Cli;

/// <summary>
/// Unit tests of <see cref="TaxonomySelector"/>: the four selection flags
/// (<c>--taxonomies</c>, <c>--taxonomykeys</c>, <c>--releases</c>, <c>--all</c>) are mutually
/// exclusive and EACH ONE interprets ONLY its own identifier. Synthetic in-memory data, no Access
/// or SQLite: no heavy fixture is needed for these rules.
///
/// The reason for keeping the flags separate (instead of accepting a code or a key in
/// <c>--taxonomies</c>) is that the merged version relied on no <c>TaxonomyCode</c> containing
/// <c>/</c>, an ACCIDENTAL property of today's data and not a guarantee of the model. These tests
/// pin that each flag fails when given the identifier of the OTHER one, precisely so that a
/// future <c>TaxonomyCode</c> with <c>/</c> (or a <c>TaxonomyKey</c> without it) does not slip
/// through silently.
/// </summary>
public sealed class TaxonomySelectorUnitTests
{
    private const int CorepId = 1;
    private const int IfId = 2;
    private const string CorepCode = "COREP 3.2";
    private const string IfCode = "IF 3.2";
    private const string CorepKey = "corep/its-005-2020/2022-03-01"; // real format
    private const string IfKey = "if/its-006-2020/2022-03-01";
    private const string Release = "3.2";

    private static IReadOnlyList<AccessTaxonomyRow> SampleTaxonomies() =>
    [
        new AccessTaxonomyRow(CorepId, 1, CorepCode, "Common Reporting", null, null, null, Release, null),
        new AccessTaxonomyRow(IfId, 2, IfCode, "Investment Firms", null, null, null, Release, null),
    ];

    private static IReadOnlyDictionary<int, string> SampleKeys() => new Dictionary<int, string>
    {
        [CorepId] = CorepKey,
        [IfId] = IfKey,
    };

    // ------------------------------------------------------------------
    // Each flag interprets ONLY its own identifier: a TaxonomyKey passed to --taxonomies, or a
    // TaxonomyCode passed to --taxonomykeys, must not find anything. It must FAIL, with its own
    // message, never slip through by a coincidence of format.
    // ------------------------------------------------------------------

    [Fact]
    public void TaxonomyCodes_WithATaxonomyKeyShapedValue_Fails()
    {
        var request = new TaxonomySelectionRequest(TaxonomyCodes: [CorepKey], TaxonomyKeys: null, ReleaseCodes: null, All: false);

        var ex = Assert.Throws<TaxonomySelectionException>(
            () => TaxonomySelector.Resolve(SampleTaxonomies(), request, SampleKeys()));

        Assert.Contains("TaxonomyCode", ex.Message, StringComparison.Ordinal);
        Assert.Contains(CorepKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TaxonomyKeys_WithATaxonomyCodeShapedValue_Fails()
    {
        var request = new TaxonomySelectionRequest(TaxonomyCodes: null, TaxonomyKeys: [CorepCode], ReleaseCodes: null, All: false);

        var ex = Assert.Throws<TaxonomySelectionException>(
            () => TaxonomySelector.Resolve(SampleTaxonomies(), request, SampleKeys()));

        Assert.Contains("TaxonomyKey", ex.Message, StringComparison.Ordinal);
        Assert.Contains(CorepCode, ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Selecting the SAME taxonomy by code and by key produces the SAME count (and the same row
    // identity).
    // ------------------------------------------------------------------

    [Fact]
    public void SelectingByCode_AndByKey_ForTheSameTaxonomy_YieldsIdenticalResults()
    {
        var byCode = TaxonomySelector.Resolve(
            SampleTaxonomies(),
            new TaxonomySelectionRequest(TaxonomyCodes: [CorepCode], TaxonomyKeys: null, ReleaseCodes: null, All: false),
            SampleKeys());

        var byKey = TaxonomySelector.Resolve(
            SampleTaxonomies(),
            new TaxonomySelectionRequest(TaxonomyCodes: null, TaxonomyKeys: [CorepKey], ReleaseCodes: null, All: false),
            SampleKeys());

        Assert.Single(byCode);
        Assert.Single(byKey);
        Assert.Equal(CorepId, byCode[0].TaxonomyId);
        Assert.Equal(CorepId, byKey[0].TaxonomyId);
    }

    // ------------------------------------------------------------------
    // Combining two selectors fails; omitting all four fails (the filter is mandatory, it is not
    // "all" by default).
    // ------------------------------------------------------------------

    [Fact]
    public void CombiningTwoSelectors_Fails()
    {
        var request = new TaxonomySelectionRequest(TaxonomyCodes: [CorepCode], TaxonomyKeys: null, ReleaseCodes: [Release], All: false);

        Assert.Throws<TaxonomySelectionException>(() => TaxonomySelector.Resolve(SampleTaxonomies(), request, SampleKeys()));
    }

    [Fact]
    public void CombiningTaxonomiesAndAll_Fails()
    {
        var request = new TaxonomySelectionRequest(TaxonomyCodes: [CorepCode], TaxonomyKeys: null, ReleaseCodes: null, All: true);

        Assert.Throws<TaxonomySelectionException>(() => TaxonomySelector.Resolve(SampleTaxonomies(), request, SampleKeys()));
    }

    [Fact]
    public void OmittingAllFourSelectors_Fails()
    {
        var request = new TaxonomySelectionRequest(TaxonomyCodes: null, TaxonomyKeys: null, ReleaseCodes: null, All: false);

        var ex = Assert.Throws<TaxonomySelectionException>(() => TaxonomySelector.Resolve(SampleTaxonomies(), request, SampleKeys()));

        Assert.Contains("--taxonomies", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--taxonomykeys", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--releases", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--all", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // --all still returns everything, without discriminating (previous behaviour, must not break).
    // ------------------------------------------------------------------

    [Fact]
    public void All_ReturnsEveryTaxonomy()
    {
        var request = new TaxonomySelectionRequest(TaxonomyCodes: null, TaxonomyKeys: null, ReleaseCodes: null, All: true);

        var result = TaxonomySelector.Resolve(SampleTaxonomies(), request, SampleKeys());

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Releases_SelectsByDpmPackageCode()
    {
        var request = new TaxonomySelectionRequest(TaxonomyCodes: null, TaxonomyKeys: null, ReleaseCodes: [Release], All: false);

        var result = TaxonomySelector.Resolve(SampleTaxonomies(), request, SampleKeys());

        Assert.Equal(2, result.Count); // both sample taxonomies belong to the same release
    }
}
