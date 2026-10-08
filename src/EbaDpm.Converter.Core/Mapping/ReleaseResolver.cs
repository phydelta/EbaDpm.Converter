namespace EbaDpm.Converter.Core.Mapping;

/// <summary>
/// Computes the dynamic release floor and the set of <c>DpmPackage</c> releases from that floor
/// upwards (the basis of <c>mRelease</c>). The floor is dynamic rather than fixed so that the
/// result can never be left unresolved when a selected taxonomy predates the fixed floor.
///
/// Access has no <c>ReleaseID</c> nor any date in <c>DpmPackage</c>: the date of each release
/// comes from crossing with <c>Taxonomy.ActualPublicationDate</c> / <c>Taxonomy.DpmPackageCode</c>,
/// ALWAYS using the calendar of <b>all</b> the taxonomies in Access (not only those selected by
/// the taxonomy filter): the publication date of a release is a fact of the Access calendar,
/// independent of which taxonomies are being converted.
///
/// The floor is dynamic, per run:
/// <code>
/// floor := the oldest, by ActualPublicationDate, between "3.4" (AbsoluteFloorReleaseCode)
///          and the oldest DPM package among the taxonomies SELECTED in this run
/// </code>
///
/// The release of a <c>Concept</c> is NOT derived from its <c>CreationDate</c>: that approach was
/// refuted, since in the reference there are concepts with identical <c>CreationDate</c> that
/// carry different releases (some suffixed, some not). The <c>ReleaseID</c> of <c>mConcept</c> is
/// derived per entity type in <c>SkeletonLoader</c>/<c>DictionaryLoader</c>, not in this class.
/// </summary>
public sealed class ReleaseResolver
{
    /// <summary>
    /// Absolute starting floor (the lower bound of the dynamic floor): it never goes below this
    /// unless a selected taxonomy belongs to a release earlier than this one.
    /// </summary>
    public const string AbsoluteFloorReleaseCode = "3.4";

    private readonly List<(string Code, DateTime Date)> _allPackagesAscending;

    /// <summary>The effective floor of this run: "3.4", or an earlier release if the taxonomy
    /// filter selected taxonomies from an older release.</summary>
    public string FloorReleaseCode { get; }

    /// <summary>Publication date of the effective floor.</summary>
    public DateTime FloorDate { get; }

    /// <summary>
    /// The packages from the floor upwards, ordered by ascending date. This is the basis of
    /// <c>mRelease</c>: the <c>ReleaseID</c> of any concept always has a row to point to.
    /// </summary>
    public IReadOnlyList<(string Code, DateTime Date)> ReleasesFromFloor { get; }

    /// <param name="allTaxonomyPublications">
    /// (DpmPackageCode, ActualPublicationDate) pairs for <b>all</b> the taxonomies in Access
    /// (without applying the taxonomy filter), with a non-null date.
    /// </param>
    /// <param name="selectedPackageCodes">
    /// The <c>DpmPackageCode</c> values of the taxonomies selected in this run (after applying
    /// the filter). They determine the dynamic floor.
    /// </param>
    /// <param name="absoluteFloorReleaseCode">
    /// The absolute floor. Configurable for tests; in production
    /// <see cref="AbsoluteFloorReleaseCode"/> is used.
    /// </param>
    public ReleaseResolver(
        IEnumerable<(string DpmPackageCode, DateTime ActualPublicationDate)> allTaxonomyPublications,
        IEnumerable<string> selectedPackageCodes,
        string absoluteFloorReleaseCode = AbsoluteFloorReleaseCode)
    {
        ArgumentNullException.ThrowIfNull(allTaxonomyPublications);
        ArgumentNullException.ThrowIfNull(selectedPackageCodes);
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteFloorReleaseCode);

        var earliestDateByPackageCode = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var (packageCode, publicationDate) in allTaxonomyPublications)
        {
            if (!earliestDateByPackageCode.TryGetValue(packageCode, out var earliest) || publicationDate < earliest)
            {
                earliestDateByPackageCode[packageCode] = publicationDate;
            }
        }

        if (!earliestDateByPackageCode.TryGetValue(absoluteFloorReleaseCode, out var absoluteFloorDate))
        {
            throw new InvalidOperationException(
                $"The absolute release floor '{absoluteFloorReleaseCode}' does not appear among the DpmPackageCode values in Access.");
        }

        var selectedCodes = selectedPackageCodes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (selectedCodes.Count == 0)
        {
            throw new ArgumentException(
                "At least one selected DpmPackageCode is required to compute the dynamic floor.",
                nameof(selectedPackageCodes));
        }

        var earliestSelected = selectedCodes
            .Select(code => (
                Code: code,
                Date: earliestDateByPackageCode.TryGetValue(code, out var date)
                    ? date
                    : throw new InvalidOperationException(
                        $"The DpmPackageCode '{code}' of a selected taxonomy does not appear in the Access calendar.")))
            .OrderBy(p => p.Date)
            .ThenBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
            .First();

        if (earliestSelected.Date < absoluteFloorDate)
        {
            FloorReleaseCode = earliestSelected.Code;
            FloorDate = earliestSelected.Date;
        }
        else
        {
            FloorReleaseCode = absoluteFloorReleaseCode;
            FloorDate = absoluteFloorDate;
        }

        _allPackagesAscending = earliestDateByPackageCode
            .Select(kv => (Code: kv.Key, Date: kv.Value))
            .OrderBy(p => p.Date)
            .ThenBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ReleasesFromFloor = _allPackagesAscending.Where(p => p.Date >= FloorDate).ToList();
    }
}
