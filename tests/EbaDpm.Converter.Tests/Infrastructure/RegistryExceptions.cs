using EbaDpm.Converter.Core.Validation;

namespace EbaDpm.Converter.Tests.Infrastructure;

/// <summary>
/// Reads the known exceptions a test applies from the single registry
/// (<see cref="KnownExceptions.All"/>), so no test keeps its own copy. Entries are selected by
/// <see cref="KnownException.Id"/> AND <see cref="KnownException.Check"/>; the result is keyed by the
/// exact <see cref="KnownException.BusinessKey"/> (never a pattern). An empty selection throws: a
/// renamed check or id must not silently empty the exemption.
/// </summary>
internal static class RegistryExceptions
{
    /// <summary>Business key -> registry reason, for the given check and exception ids.</summary>
    public static IReadOnlyDictionary<string, string> ByKey(string check, params string[] ids)
    {
        var selected = KnownExceptions.All
            .Where(e => e.Check == check && ids.Contains(e.Id, StringComparer.Ordinal))
            .ToList();

        if (selected.Count == 0)
        {
            throw new InvalidOperationException(
                $"The registry (KnownExceptions.All) has no entry for Id(s) [{string.Join(", ", ids)}] on Check '{check}': "
                + "the exemption would be silently empty (renamed check or id?).");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in selected)
        {
            if (!result.TryAdd(entry.BusinessKey, $"{entry.Id}: {entry.Reason}"))
            {
                throw new InvalidOperationException(
                    $"Registry key '{entry.BusinessKey}' appears twice for Check '{check}'.");
            }
        }

        return result;
    }
}
