using System.Data.OleDb;
using System.Runtime.InteropServices;
using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Tests.Dpm2;

namespace EbaDpm.Converter.Tests.Access;

/// <summary>
/// ACE provider selection. <see cref="AceProvider.Select"/> is pure, so it is tested with recorded
/// <c>tryOpen</c> calls. Every "not called" assertion is paired with a test that shows the same
/// provider IS called when it is registered and first in line.
/// </summary>
public sealed class AceProviderSelectionTests
{
    private const string P16 = "Microsoft.ACE.OLEDB.16.0";
    private const string P12 = "Microsoft.ACE.OLEDB.12.0";
    private const string AccdbPath = @"C:\data\some.accdb";

    private static string Run(string[] registered, Func<string, Exception?> open, out List<string> calls)
    {
        var recorded = new List<string>();
        var result = AceProvider.Select(registered, p => { recorded.Add(p); return open(p); }, AccdbPath, false);
        calls = recorded;
        return result;
    }

    private static AceProviderNotFoundException RunFail(string[] registered, Func<string, Exception?> open,
        bool other, out List<string> calls)
    {
        var recorded = new List<string>();
        var ex = Assert.Throws<AceProviderNotFoundException>(
            () => AceProvider.Select(registered, p => { recorded.Add(p); return open(p); }, AccdbPath, other));
        calls = recorded;
        return ex;
    }

    [Fact]
    public void Candidates_AreSixteenThenTwelve() =>
        Assert.Equal(new[] { P16, P12 }, AceProvider.Candidates.ToArray());

    [Fact]
    public void BothRegisteredAndOpen_Picks16_AndNeverTriesTwelve()
    {
        var chosen = Run([P16, P12], _ => null, out var calls);
        Assert.Equal(P16, chosen);
        Assert.Equal(new[] { P16 }, calls);
    }

    [Fact]
    public void OnlyTwelveRegistered_PicksTwelve_AndNeverTriesSixteen()
    {
        var chosen = Run([P12], _ => null, out var calls);
        Assert.Equal(P12, chosen);
        Assert.Equal(new[] { P12 }, calls);
        Assert.DoesNotContain(P16, calls);
        // Positive control: the same provider IS tried when it is registered.
        Run([P16, P12], _ => null, out var callsWith16);
        Assert.Contains(P16, callsWith16);
    }

    [Fact]
    public void SixteenRegisteredButOpenFails_FallsBackToTwelve()
    {
        var chosen = Run([P16, P12], p => p == P16 ? new InvalidOperationException("boom16") : null, out var calls);
        Assert.Equal(P12, chosen);
        Assert.Equal(new[] { P16, P12 }, calls);
    }

    [Fact]
    public void RegisteredNamesAreMatchedCaseInsensitively()
    {
        var chosen = Run(["MICROSOFT.ace.oledb.16.0"], _ => null, out var calls);
        Assert.Equal(P16, chosen);
        Assert.Equal(new[] { P16 }, calls);
        var chosen12 = Run(["microsoft.ACE.OLEDB.12.0"], _ => null, out _);
        Assert.Equal(P12, chosen12);
    }

    [Fact]
    public void NothingRegistered_Throws_WithBothAttemptsNotRegistered_AndNamesFile()
    {
        var ex = RunFail([], _ => null, other: false, out var calls);
        Assert.Empty(calls);
        Assert.Equal(2, ex.Attempts.Count);
        Assert.Equal(P16, ex.Attempts[0].Provider);
        Assert.Equal(P12, ex.Attempts[1].Provider);
        Assert.All(ex.Attempts, a => Assert.Contains("not registered", a.Outcome, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(AccdbPath, ex.Message, StringComparison.Ordinal);
        Assert.Contains(RuntimeInformation.ProcessArchitecture.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(ex.OtherArchitectureHasProvider);
    }

    [Fact]
    public void NothingRegistered_OtherArchitectureHasProvider_MessagePointsToOtherExecutable()
    {
        var ex = RunFail([], _ => null, other: true, out _);
        Assert.True(ex.OtherArchitectureHasProvider);
        Assert.Contains(AccdbPath, ex.Message, StringComparison.Ordinal);
        Assert.Contains("architecture", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("32-bit", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("64-bit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NothingRegistered_NoOtherArchitecture_MessageSaysInstallOffice()
    {
        var ex = RunFail([], _ => null, other: false, out _);
        Assert.Contains("install", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Office", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Access database engine", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Positive control: the two messages differ, so the keyword checks are not satisfied by both.
        var other = RunFail([], _ => null, other: true, out _);
        Assert.NotEqual(ex.Message, other.Message);
    }

    [Fact]
    public void BothRegisteredBothFail_AttemptsCarryEachOpenError()
    {
        var ex = RunFail([P16, P12],
            p => new InvalidOperationException("open-error-for-" + p), other: false, out var calls);
        Assert.Equal(new[] { P16, P12 }, calls);
        Assert.Equal(2, ex.Attempts.Count);
        Assert.Equal(P16, ex.Attempts[0].Provider);
        Assert.Equal(P12, ex.Attempts[1].Provider);
        Assert.Contains("open-error-for-" + P16, ex.Attempts[0].Outcome, StringComparison.Ordinal);
        Assert.Contains("open-error-for-" + P12, ex.Attempts[1].Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public void OneRegisteredFails_OtherNotRegistered_AttemptsDistinguishThem()
    {
        var ex = RunFail([P16], _ => new InvalidOperationException("only-16-failed"), other: false, out var calls);
        Assert.Equal(new[] { P16 }, calls);
        Assert.Contains("only-16-failed", ex.Attempts[0].Outcome, StringComparison.Ordinal);
        Assert.Contains("not registered", ex.Attempts[1].Outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonAceProvider_IsNeverChosen_AndNeverTried()
    {
        var ex = RunFail(["Microsoft.Jet.OLEDB.4.0"], _ => null, other: false, out var calls);
        Assert.Empty(calls);
        Assert.Equal(2, ex.Attempts.Count);
        // Positive control: with an ACE provider alongside, the ACE one is chosen, not the Jet one.
        var chosen = Run(["Microsoft.Jet.OLEDB.4.0", P12], _ => null, out var calls2);
        Assert.Equal(P12, chosen);
        Assert.Equal(new[] { P12 }, calls2);
    }

    [AceFact]
    public void RealProvider_RegisteredContainsACandidate()
    {
        var registered = AceProvider.RegisteredForCurrentProcess();
        Assert.Contains(registered, r => AceProvider.Candidates.Contains(r, StringComparer.OrdinalIgnoreCase));
    }

    [AceFact]
    public void RealProvider_ResolveAndConnectionString_UseACandidate()
    {
        var path = Dpm2TestAccdbFactory.CreateEmptyAccdb();
        try
        {
            var resolved = AceProvider.Resolve(path);
            Assert.Contains(resolved, AceProvider.Candidates);
            Assert.StartsWith("Provider=" + resolved, AceProvider.ConnectionString(path), StringComparison.Ordinal);
            Assert.Contains(path, AceProvider.ConnectionString(path), StringComparison.Ordinal);
        }
        finally
        {
            OleDbConnection.ReleaseObjectPool();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            System.IO.File.Delete(path);
        }
    }
}
