using EbaDpm.Converter.Core.Access;

namespace EbaDpm.Converter.Core.Mapping;

/// <summary>
/// Taxonomy selection request as it arrives from the CLI. Exactly one of the four forms must be
/// present: <see cref="TaxonomyCodes"/>, <see cref="TaxonomyKeys"/>, <see cref="ReleaseCodes"/> or
/// <see cref="All"/>. Identifiers are not mixed: <see cref="TaxonomyCodes"/> selects only by
/// <c>TaxonomyCode</c> and <see cref="TaxonomyKeys"/> only by <c>TaxonomyKey</c>.
/// </summary>
public sealed record TaxonomySelectionRequest(
    IReadOnlyList<string>? TaxonomyCodes,
    IReadOnlyList<string>? TaxonomyKeys,
    IReadOnlyList<string>? ReleaseCodes,
    bool All);

/// <summary>
/// Taxonomy selection error: missing filter, ambiguous filter, or a code/release that does not
/// exist in Access. It always carries a clear message with the list of available values.
/// </summary>
public sealed class TaxonomySelectionException : Exception
{
    public TaxonomySelectionException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Resolves the mandatory taxonomy filter against the real <c>Taxonomy</c> census read from
/// Access. Without <c>--taxonomies</c>, <c>--taxonomykeys</c>, <c>--releases</c> or <c>--all</c>,
/// the conversion fails listing the available taxonomies. A code, key or release that does not
/// exist fails the same way; it is never silently ignored.
///
/// Each identifier has its own flag and they are not mixed. <c>--taxonomies</c> selects only by
/// <c>TaxonomyCode</c>; <c>--taxonomykeys</c> selects only by <c>TaxonomyKey</c>. Accepting both
/// in the same list was deliberately rejected: it relied on no <c>TaxonomyCode</c> containing
/// <c>/</c>, an accidental property of today's data rather than a guarantee of the model.
/// </summary>
public static class TaxonomySelector
{
    /// <param name="available">Unfiltered <c>Taxonomy</c> census read from Access.</param>
    /// <param name="request">Selection request as it arrives from the CLI.</param>
    /// <param name="taxonomyKeyByTaxonomyId">
    /// <c>TaxonomyKey</c> of each taxonomy, as produced by
    /// <see cref="TaxonomyKeyResolver.Resolve"/>. Required and non-empty when
    /// <see cref="TaxonomySelectionRequest.TaxonomyKeys"/> has any value: without it there is no
    /// way to resolve the selection by key, and resolving against an empty map would find nothing
    /// without the caller noticing. It may be omitted for the other selectors.
    /// </param>
    public static IReadOnlyList<AccessTaxonomyRow> Resolve(
        IReadOnlyList<AccessTaxonomyRow> available,
        TaxonomySelectionRequest request,
        IReadOnlyDictionary<int, string>? taxonomyKeyByTaxonomyId = null)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(request);

        var hasCodes = request.TaxonomyCodes is { Count: > 0 };
        var hasKeys = request.TaxonomyKeys is { Count: > 0 };
        var hasReleases = request.ReleaseCodes is { Count: > 0 };
        var specifiedCount = (hasCodes ? 1 : 0) + (hasKeys ? 1 : 0) + (hasReleases ? 1 : 0) + (request.All ? 1 : 0);

        if (specifiedCount == 0)
        {
            throw new TaxonomySelectionException(
                "You must specify --taxonomies, --taxonomykeys, --releases or --all: the taxonomy filter is mandatory."
                + Environment.NewLine + Environment.NewLine
                + BuildAvailableTaxonomiesList(available, taxonomyKeyByTaxonomyId ?? new Dictionary<int, string>()));
        }

        if (specifiedCount > 1)
        {
            throw new TaxonomySelectionException(
                "Only one of --taxonomies, --taxonomykeys, --releases or --all can be used at a time.");
        }

        if (request.All)
        {
            return available;
        }

        if (hasCodes)
        {
            return ResolveByTaxonomyCode(available, taxonomyKeyByTaxonomyId ?? new Dictionary<int, string>(), request.TaxonomyCodes!);
        }

        if (hasKeys)
        {
            if (taxonomyKeyByTaxonomyId is null || taxonomyKeyByTaxonomyId.Count == 0)
            {
                throw new TaxonomySelectionException(
                    "Selection by --taxonomykeys was requested but the TaxonomyKey map was not "
                    + "provided (or is empty); without it the selection by key cannot be resolved.");
            }

            return ResolveByTaxonomyKey(available, taxonomyKeyByTaxonomyId, request.TaxonomyKeys!);
        }

        return ResolveByReleaseCode(available, request.ReleaseCodes!);
    }

    private static IReadOnlyList<AccessTaxonomyRow> ResolveByTaxonomyCode(
        IReadOnlyList<AccessTaxonomyRow> available,
        IReadOnlyDictionary<int, string> taxonomyKeyByTaxonomyId,
        IReadOnlyList<string> requestedCodes)
    {
        var byCode = available.ToLookup(t => t.TaxonomyCode, StringComparer.OrdinalIgnoreCase);

        var selected = new List<AccessTaxonomyRow>();
        var missing = new List<string>();

        foreach (var code in requestedCodes)
        {
            var matches = byCode[code].ToList();
            if (matches.Count == 0)
            {
                missing.Add(code);
                continue;
            }

            selected.AddRange(matches);
        }

        if (missing.Count > 0)
        {
            throw new TaxonomySelectionException(
                $"No taxonomy exists with the code (TaxonomyCode) {string.Join(", ", missing)}."
                + Environment.NewLine + Environment.NewLine
                + BuildAvailableTaxonomiesList(available, taxonomyKeyByTaxonomyId));
        }

        return selected
            .GroupBy(t => t.TaxonomyId)
            .Select(g => g.First())
            .OrderBy(t => t.TaxonomyId)
            .ToList();
    }

    private static IReadOnlyList<AccessTaxonomyRow> ResolveByTaxonomyKey(
        IReadOnlyList<AccessTaxonomyRow> available,
        IReadOnlyDictionary<int, string> taxonomyKeyByTaxonomyId,
        IReadOnlyList<string> requestedKeys)
    {
        var byKey = available
            .Where(t => taxonomyKeyByTaxonomyId.ContainsKey(t.TaxonomyId))
            .ToLookup(t => taxonomyKeyByTaxonomyId[t.TaxonomyId], StringComparer.OrdinalIgnoreCase);

        var selected = new List<AccessTaxonomyRow>();
        var missing = new List<string>();

        foreach (var key in requestedKeys)
        {
            var matches = byKey[key].ToList();
            if (matches.Count == 0)
            {
                missing.Add(key);
                continue;
            }

            selected.AddRange(matches);
        }

        if (missing.Count > 0)
        {
            throw new TaxonomySelectionException(
                $"No taxonomy exists with the key (TaxonomyKey) {string.Join(", ", missing)}."
                + Environment.NewLine + Environment.NewLine
                + BuildAvailableTaxonomiesList(available, taxonomyKeyByTaxonomyId));
        }

        return selected
            .GroupBy(t => t.TaxonomyId)
            .Select(g => g.First())
            .OrderBy(t => t.TaxonomyId)
            .ToList();
    }

    private static IReadOnlyList<AccessTaxonomyRow> ResolveByReleaseCode(
        IReadOnlyList<AccessTaxonomyRow> available, IReadOnlyList<string> requestedReleases)
    {
        var availableReleaseCodes = available
            .Select(t => t.DpmPackageCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missing = requestedReleases
            .Where(r => !availableReleaseCodes.Contains(r, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (missing.Count > 0)
        {
            var availableList = string.Join(", ", availableReleaseCodes.OrderBy(r => r, StringComparer.OrdinalIgnoreCase));
            throw new TaxonomySelectionException(
                $"Release(s) not found in Access: {string.Join(", ", missing)}. "
                + $"Available releases (DpmPackageCode): {availableList}.");
        }

        var wanted = new HashSet<string>(requestedReleases, StringComparer.OrdinalIgnoreCase);
        return available
            .Where(t => wanted.Contains(t.DpmPackageCode))
            .OrderBy(t => t.TaxonomyId)
            .ToList();
    }

    private static string BuildAvailableTaxonomiesList(
        IReadOnlyList<AccessTaxonomyRow> available, IReadOnlyDictionary<int, string> taxonomyKeyByTaxonomyId)
    {
        var lines = available
            .Select(t => (Key: taxonomyKeyByTaxonomyId.GetValueOrDefault(t.TaxonomyId, "-"), t.TaxonomyCode))
            .OrderBy(t => t.TaxonomyCode, StringComparer.OrdinalIgnoreCase)
            .Select(t => $"{t.Key} ({t.TaxonomyCode})");

        return "Available taxonomies (" + available.Count + "):" + Environment.NewLine
            + string.Join(Environment.NewLine, lines);
    }
}
