using EbaDpm.Converter.Core.Validation;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Synthetic tests (no data files) of <see cref="KnownExceptions.ApplyContainmentExceptions"/>:
/// both <c>Defect</c> and <c>DeclaredDivergence</c> exceptions of the exact check id must be applied.
/// </summary>
public sealed class KnownExceptionsContainmentTests
{
    private const string CheckId = "B-DIC-01-DIMENSION-CODE";

    [Fact]
    public void DeclaredDivergence_Dd26_ResolvesTns_AndIsMatched()
    {
        var reference = new HashSet<string>(["TNS", "X"], StringComparer.Ordinal);
        var missing = new HashSet<string>(["TNS"], StringComparer.Ordinal);

        var (unresolved, outcomes) = KnownExceptions.ApplyContainmentExceptions(
            CheckId, "EBA_4.2_Hotfix.db", missing, reference, ValidationPlane.B);

        Assert.DoesNotContain("TNS", unresolved);
        Assert.Empty(unresolved);
        var dd26 = Assert.Single(outcomes, o => o.Exception.Id == "DD-26");
        Assert.Equal("TNS", dd26.Exception.BusinessKey);
        Assert.True(dd26.AppliesToThisReference);
        Assert.Equal(1, dd26.Matched);
        Assert.False(dd26.Stale);
    }

    [Fact]
    public void DeclaredDivergence_Dd26_TnsNotMissingAnymore_IsStale_AndOtherMissingStaysUnresolved()
    {
        // Positive control of the negative path: TNS is in the reference but no longer missing.
        var reference = new HashSet<string>(["TNS", "X"], StringComparer.Ordinal);
        var missing = new HashSet<string>(["X"], StringComparer.Ordinal);

        var (unresolved, outcomes) = KnownExceptions.ApplyContainmentExceptions(
            CheckId, "EBA_4.2_Hotfix.db", missing, reference, ValidationPlane.B);

        Assert.Equal(["X"], unresolved);
        var dd26 = Assert.Single(outcomes, o => o.Exception.Id == "DD-26");
        Assert.Equal(0, dd26.Matched);
        Assert.True(dd26.Stale);
    }

    [Fact]
    public void Defect_E4_Template_StillApplies_ForItsOwnReference()
    {
        var reference = new HashSet<string>(["template", "X"], StringComparer.Ordinal);
        var missing = new HashSet<string>(["template"], StringComparer.Ordinal);

        var (unresolved, outcomes) = KnownExceptions.ApplyContainmentExceptions(
            CheckId, "EBA_3.2_phase_1.db", missing, reference, ValidationPlane.B);

        Assert.Empty(unresolved);
        var e4 = Assert.Single(outcomes, o => o.Exception.Id == "E-4");
        Assert.Equal("template", e4.Exception.BusinessKey);
        Assert.True(e4.AppliesToThisReference);
        Assert.Equal(1, e4.Matched);
        Assert.False(e4.Stale);
    }

    [Fact]
    public void Dd26_DoesNotApplyToAnotherReference_AndTnsStaysUnresolved()
    {
        var reference = new HashSet<string>(["TNS", "X"], StringComparer.Ordinal);
        var missing = new HashSet<string>(["TNS"], StringComparer.Ordinal);

        var (unresolved, outcomes) = KnownExceptions.ApplyContainmentExceptions(
            CheckId, "EBA_3.2_phase_1.db", missing, reference, ValidationPlane.B);

        Assert.Equal(["TNS"], unresolved);
        var dd26 = Assert.Single(outcomes, o => o.Exception.Id == "DD-26");
        Assert.False(dd26.AppliesToThisReference);
        Assert.Equal(0, dd26.Matched);
    }
}
