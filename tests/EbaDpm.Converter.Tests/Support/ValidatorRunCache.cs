using System.Collections.Concurrent;
using EbaDpm.Converter.Core.Validation;

namespace EbaDpm.Converter.Tests.Support;

/// <summary>
/// <see cref="Validator.Run"/> is a pure function of the triple
/// <c>(generatedPath, referencePath, layoutsPath)</c> and takes about 20 s. Without this cache it
/// would be invoked once PER TEST on the SAME report. The cache memoises by that triple so that
/// each combination is computed only ONCE per run.
///
/// It deliberately lives OUTSIDE each fixture: several independent test files (all on top of
/// <see cref="Dpm2.Dpm20SkeletonFixture"/>) call <c>Validator.Run</c> with the SAME triple without
/// sharing any base class, so memoising inside one particular fixture would not catch those
/// cross-file cases.
///
/// It has no effect on correctness: <see cref="Validator.Result"/> is a <c>record</c> whose
/// <c>Report</c> (<c>ValidationReport</c>) exposes <see cref="IReadOnlyList{T}"/> lists and
/// immutable nested records, so sharing the SAME instance between tests cannot leak a mutation
/// from one test to another. Collections already run serially
/// (<c>parallelizeTestCollections=false</c> in <c>xunit.runner.json</c>: ACE OLEDB does not support
/// concurrent opening of the Access database from several threads of the same process), so there
/// is no real race when filling the cache; <see cref="ConcurrentDictionary{TKey,TValue}"/> is used
/// for hygiene, not because serial execution requires it.
/// </summary>
internal static class ValidatorRunCache
{
    private static readonly ConcurrentDictionary<(string GeneratedPath, string? ReferencePath, string? LayoutsPath), Validator.Result> Cache = new();

    public static Validator.Result Run(string generatedPath, string? referencePath, string? layoutsPath = null)
    {
        var key = (generatedPath, referencePath, layoutsPath);
        return Cache.GetOrAdd(key, static k => Validator.Run(k.GeneratedPath, k.ReferencePath, k.LayoutsPath));
    }
}
