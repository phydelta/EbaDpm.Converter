using Xunit.Sdk;

namespace EbaDpm.Converter.Tests.Infrastructure;

/// <summary>
/// Positive control of the stale check of <see cref="DoraAbsentFromReference42"/>: the exception holds only
/// while the excluded objects are in the generated side and absent from the reference export.
/// Synthetic, no data files.
/// </summary>
public sealed class DoraAbsentFromReference42Tests
{
    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.Ordinal);

    [Fact]
    public void Taxonomy_InGeneratedAndAbsentFromReference_IsNotStale()
        => DoraAbsentFromReference42.AssertTaxonomyStale(Set("finrep 4.2", "dora 4.2"), Set("finrep 4.2"));

    [Fact]
    public void Taxonomy_PresentInTheReference_IsStale()
        => Assert.Throws<TrueException>(
            () => DoraAbsentFromReference42.AssertTaxonomyStale(Set("finrep 4.2", "dora 4.2"), Set("finrep 4.2", "dora 4.2")));

    [Fact]
    public void Taxonomy_AbsentFromTheGeneratedSide_IsStale()
        => Assert.Throws<TrueException>(
            () => DoraAbsentFromReference42.AssertTaxonomyStale(Set("finrep 4.2"), Set("finrep 4.2")));

    [Fact]
    public void Framework_InGeneratedAndAbsentFromReference_IsNotStale()
        => DoraAbsentFromReference42.AssertFrameworkStale(Set("FINREP", "DORA"), Set("FINREP"));

    [Fact]
    public void Framework_PresentInTheReference_IsStale()
        => Assert.Throws<TrueException>(
            () => DoraAbsentFromReference42.AssertFrameworkStale(Set("FINREP", "DORA"), Set("FINREP", "DORA")));

    [Fact]
    public void Framework_AbsentFromTheGeneratedSide_IsStale()
        => Assert.Throws<TrueException>(
            () => DoraAbsentFromReference42.AssertFrameworkStale(Set("FINREP"), Set("FINREP")));
}
