using System.Globalization;
using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Access.Dpm20;

namespace EbaDpm.Converter.Core.Mapping.Dpm20;

/// <summary>
/// The five derived columns of one <c>mTaxonomy</c> row. A null member means "not resolvable from
/// the source" (the reason is in <see cref="Dpm20TaxonomyMetadataResult.UnresolvedCases"/>).
/// </summary>
public sealed record Dpm20TaxonomyMetadata(
    string? TaxonomyLabel,
    string? Version,
    string? PublicationDate,
    string? FromDate,
    string? ToDate);

/// <summary>Result of <see cref="Dpm20TaxonomyMetadataCalculator.Compute"/>.</summary>
/// <param name="ByTaxonomyId">The metadata of each taxonomy passed in, by <c>TaxonomyId</c>.</param>
/// <param name="UnresolvedCases">One line per taxonomy column that could not be derived.</param>
public sealed record Dpm20TaxonomyMetadataResult(
    IReadOnlyDictionary<int, Dpm20TaxonomyMetadata> ByTaxonomyId,
    IReadOnlyList<string> UnresolvedCases);

/// <summary>
/// Derives <c>mTaxonomy.TaxonomyLabel</c>, <c>Version</c>, <c>PublicationDate</c>, <c>FromDate</c>
/// and <c>ToDate</c> of a DPM 2.0 taxonomy T = (framework F, release R) from the
/// <c>ModuleVersion</c> history up to the cutoff release (pure function over rows).
///
/// Release order is <c>ReleaseID</c> order; only releases up to the cutoff are considered. A module
/// version "starts in S" when <c>StartReleaseID = S</c> and is "current at R" when
/// <c>Start &lt;= R AND (End IS NULL OR End &gt; R)</c> (End exclusive).
/// <list type="bullet">
/// <item>R' = the greatest release S &lt;= R in which some module version of F starts AND is
/// current at R (R' = R when F publishes module versions in R).</item>
/// <item><c>Version</c> = <c>Release.Code</c> of R; <c>PublicationDate</c> = <c>Release.Date</c> of
/// R, ISO text as is.</item>
/// <item><c>TaxonomyLabel</c> = <c>&lt;Framework.Name&gt; &lt;v&gt; (DPM &lt;Release.Code of R&gt;)</c>,
/// v = highest <c>VersionNumber</c> (numeric <see cref="System.Version"/> comparison, written as
/// stored) among ALL module versions of F that start in R'.</item>
/// <item><c>FromDate</c> = lowest <c>FromReferenceDate</c> among the module versions of F starting
/// in R'.</item>
/// <item><c>ToDate</c> = (FromDate of the first "publication release" S &gt; R' of F whose FromDate
/// is strictly later than FromDate(T)) minus one day; <see cref="OpenEndedToDate"/> when none.</item>
/// </list>
/// </summary>
public static class Dpm20TaxonomyMetadataCalculator
{
    /// <summary>
    /// <c>ToDate</c> of a taxonomy that no later publication of its framework supersedes. An
    /// intentional open-ended sentinel, NOT a date declared by the source.
    /// </summary>
    public const string OpenEndedToDate = "9999-12-31";

    private const string DateFormat = "yyyy-MM-dd";

    private sealed record Publication(int StartReleaseId, Version? Version, string? VersionText, DateOnly? FromDate);

    public static Dpm20TaxonomyMetadataResult Compute(
        IReadOnlyList<Dpm20ReleaseRow> releases,
        IReadOnlyList<Dpm20FrameworkRow> frameworks,
        IReadOnlyList<Dpm20ModuleVersionHistoryRow> moduleVersionHistory,
        IReadOnlyList<AccessTaxonomyRow> taxonomies,
        int cutoffReleaseId)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(frameworks);
        ArgumentNullException.ThrowIfNull(moduleVersionHistory);
        ArgumentNullException.ThrowIfNull(taxonomies);

        var releaseByCode = releases.ToDictionary(r => r.Code, r => r, StringComparer.Ordinal);
        var frameworkById = frameworks.ToDictionary(f => f.FrameworkId);
        var unresolved = new List<string>();
        var result = new Dictionary<int, Dpm20TaxonomyMetadata>();

        // Module versions per framework, only up to the cutoff.
        var historyByFramework = moduleVersionHistory
            .Where(mv => mv.StartReleaseId <= cutoffReleaseId)
            .GroupBy(mv => mv.FrameworkId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Publication releases of a framework (one per release in which a module version starts),
        // computed once per framework.
        var publicationsByFramework = new Dictionary<int, List<Publication>>();

        List<Publication> PublicationsOf(int frameworkId)
        {
            if (publicationsByFramework.TryGetValue(frameworkId, out var cached))
            {
                return cached;
            }

            var list = new List<Publication>();
            if (historyByFramework.TryGetValue(frameworkId, out var rows))
            {
                foreach (var group in rows.GroupBy(r => r.StartReleaseId).OrderBy(g => g.Key))
                {
                    Version? highest = null;
                    string? highestText = null;
                    DateOnly? lowestFrom = null;

                    foreach (var row in group)
                    {
                        if (row.VersionNumber is { } text && System.Version.TryParse(text.Trim(), out var parsed)
                            && (highest is null || parsed > highest))
                        {
                            highest = parsed;
                            highestText = text.Trim();
                        }

                        if (TryNormalizeDate(row.FromReferenceDate, out var from)
                            && (lowestFrom is null || from < lowestFrom))
                        {
                            lowestFrom = from;
                        }
                    }

                    list.Add(new Publication(group.Key, highest, highestText, lowestFrom));
                }
            }

            publicationsByFramework[frameworkId] = list;
            return list;
        }

        foreach (var taxonomy in taxonomies)
        {
            if (taxonomy.FrameworkId is not { } frameworkId
                || !frameworkById.TryGetValue(frameworkId, out var framework)
                || !releaseByCode.TryGetValue(taxonomy.DpmPackageCode, out var release))
            {
                unresolved.Add($"{taxonomy.TaxonomyCode}: framework or release not found; all five columns left NULL.");
                result[taxonomy.TaxonomyId] = new Dpm20TaxonomyMetadata(null, null, null, null, null);
                continue;
            }

            // R' — greatest release <= R with a module version of F that starts there and is current at R.
            int? rPrime = null;
            if (historyByFramework.TryGetValue(frameworkId, out var frameworkRows))
            {
                rPrime = frameworkRows
                    .Where(row => row.StartReleaseId <= release.ReleaseId
                        && (row.EndReleaseId is null || row.EndReleaseId > release.ReleaseId))
                    .Select(row => (int?)row.StartReleaseId)
                    .Max();
            }

            // Version and PublicationDate depend on R only.
            var publicationDate = release.Date;
            if (publicationDate is null)
            {
                unresolved.Add($"{taxonomy.TaxonomyCode}: Release.Date of '{release.Code}' is NULL; PublicationDate left NULL.");
            }

            if (rPrime is null)
            {
                unresolved.Add(
                    $"{taxonomy.TaxonomyCode}: no module version of framework '{framework.Code}' starts at or before "
                    + $"release '{release.Code}' and is current there; TaxonomyLabel, FromDate and ToDate left NULL.");
                result[taxonomy.TaxonomyId] = new Dpm20TaxonomyMetadata(null, release.Code, publicationDate, null, null);
                continue;
            }

            var rPrimeId = rPrime.Value;
            var publications = PublicationsOf(frameworkId);
            var own = publications.First(p => p.StartReleaseId == rPrimeId);

            string? label = null;
            if (own.VersionText is null)
            {
                unresolved.Add($"{taxonomy.TaxonomyCode}: no usable ModuleVersion.VersionNumber in release ReleaseID={rPrimeId}; TaxonomyLabel left NULL.");
            }
            else
            {
                label = $"{framework.Name} {own.VersionText} (DPM {release.Code})";
            }

            string? fromText = null;
            string? toText = null;
            if (own.FromDate is not { } fromDate)
            {
                unresolved.Add($"{taxonomy.TaxonomyCode}: no usable ModuleVersion.FromReferenceDate in release ReleaseID={rPrimeId}; FromDate and ToDate left NULL.");
            }
            else
            {
                fromText = fromDate.ToString(DateFormat, CultureInfo.InvariantCulture);

                var next = publications
                    .Where(p => p.StartReleaseId > rPrimeId && p.FromDate is { } f && f > fromDate)
                    .OrderBy(p => p.StartReleaseId)
                    .FirstOrDefault();

                toText = next is { FromDate: { } nextFrom }
                    ? nextFrom.AddDays(-1).ToString(DateFormat, CultureInfo.InvariantCulture)
                    : OpenEndedToDate;
            }

            result[taxonomy.TaxonomyId] = new Dpm20TaxonomyMetadata(label, release.Code, publicationDate, fromText, toText);
        }

        return new Dpm20TaxonomyMetadataResult(result, unresolved);
    }

    /// <summary>Normalises a source date text to a <see cref="DateOnly"/>; false when empty or unparsable.</summary>
    internal static bool TryNormalizeDate(string? text, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return DateOnly.TryParseExact(
            text.Trim().Length >= 10 ? text.Trim()[..10] : text.Trim(),
            DateFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }
}
