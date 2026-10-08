using System.Text.RegularExpressions;
using EbaDpm.Converter.Core.Access;

namespace EbaDpm.Converter.Core.Mapping;

/// <summary>
/// Error deriving the <c>TaxonomyKey</c> from <c>Module.XbrlSchemaRef</c>: a module with no
/// extractable segment, or a single taxonomy whose modules would yield different keys. Both cases
/// mean the source has changed relative to what is assumed here and must stop the run, never be
/// resolved silently with an arbitrary key.
/// </summary>
public sealed class TaxonomyKeyResolutionException : Exception
{
    public TaxonomyKeyResolutionException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Derives the <c>TaxonomyKey</c>: the selection identifier of a taxonomy, distinct from
/// <c>TaxonomyCode</c>. It is the segment of <c>Module.XbrlSchemaRef</c> between <c>/fws/</c> and
/// <c>/mod/</c>, and it covers both source forms without distinguishing cases: 3 segments up to
/// 3.x (<c>corep/its-005-2020/2024-07-11</c>) and 2 segments from 4.0 on (<c>sbp/4.0</c>).
///
/// Measured on the full Access: 415 modules, 0 without an extractable key, 128 distinct keys for
/// 128 taxonomies, 0 keys shared by two taxonomies and 0 taxonomies with two keys. All the
/// modules of a taxonomy yield the same key, so it is derived once per taxonomy.
/// </summary>
public static class TaxonomyKeyResolver
{
    private static readonly Regex KeyPattern = new(
        "/fws/(?<key>.+?)/mod/", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Derives the <c>TaxonomyKey</c> of each taxonomy from its modules. Throws
    /// <see cref="TaxonomyKeyResolutionException"/> if any module has no extractable key or if
    /// the modules of a single taxonomy yield different keys.
    /// </summary>
    public static IReadOnlyDictionary<int, string> Resolve(IEnumerable<AccessModuleRow> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var keyByTaxonomyId = new Dictionary<int, string>();

        foreach (var module in modules)
        {
            var key = ExtractKey(module);

            if (keyByTaxonomyId.TryGetValue(module.TaxonomyId, out var existingKey))
            {
                if (!string.Equals(existingKey, key, StringComparison.Ordinal))
                {
                    throw new TaxonomyKeyResolutionException(
                        $"TaxonomyID={module.TaxonomyId} has modules with different TaxonomyKey values: "
                        + $"'{existingKey}' and '{key}' (the latter from ModuleID={module.ModuleId}, "
                        + $"XbrlSchemaRef='{module.XbrlSchemaRef}'). The source has changed relative "
                        + "to the assumption that all the modules of a taxonomy yield the same key.");
                }

                continue;
            }

            keyByTaxonomyId[module.TaxonomyId] = key;
        }

        return keyByTaxonomyId;
    }

    private static string ExtractKey(AccessModuleRow module)
    {
        var schemaRef = module.XbrlSchemaRef;
        var match = schemaRef is null ? null : KeyPattern.Match(schemaRef);

        if (match is not { Success: true })
        {
            throw new TaxonomyKeyResolutionException(
                $"Could not extract TaxonomyKey from Module.XbrlSchemaRef (ModuleID={module.ModuleId}, "
                + $"TaxonomyID={module.TaxonomyId}, XbrlSchemaRef='{schemaRef}'). A segment was expected "
                + "between '/fws/' and '/mod/'.");
        }

        return match.Groups["key"].Value;
    }
}
