using Xunit.Abstractions;
using Xunit.Sdk;

namespace EbaDpm.Converter.Tests.Dictionary;

/// <summary>
/// Positive controls for <see cref="GoldenDiffTests.AssertContainsReference"/>, the single method
/// used by the data tests. No data files: every case is built synthetically, so a zero from the
/// data tests is shown to be able to be non-zero.
/// </summary>
public sealed class GoldenDiffAssertionSyntheticTests
{
    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);

    private static Dictionary<string, string> Exceptions(params string[] codes) =>
        codes.ToDictionary(c => c, _ => "synthetic reason", StringComparer.Ordinal);

    [Fact]
    public void ValidException_PassesAndIsReported()
    {
        var output = new CapturingOutput();

        // 'x' is in the reference, absent from the output, and registered: the valid case.
        GoldenDiffTests.AssertContainsReference("t", Set("a"), Set("a", "x"), Exceptions("x"), output);

        Assert.Contains(output.Lines, l => l.Contains("exceptions applied: x (synthetic reason)", StringComparison.Ordinal));
    }

    [Fact]
    public void NoException_AndFullCoverage_Passes() =>
        GoldenDiffTests.AssertContainsReference("t", Set("a", "b"), Set("a", "b"));

    [Fact]
    public void MissingCodeWithoutException_Fails()
    {
        var ex = Assert.ThrowsAny<XunitException>(
            () => GoldenDiffTests.AssertContainsReference("t", Set("a"), Set("a", "b")));
        Assert.Contains("NO registered exception", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExceptionAbsentFromReference_FailsAsStale()
    {
        var ex = Assert.ThrowsAny<XunitException>(
            () => GoldenDiffTests.AssertContainsReference("t", Set("a"), Set("a"), Exceptions("gone")));
        Assert.Contains("STALE", ex.Message, StringComparison.Ordinal);
        Assert.Contains("gone", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no longer correspond", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExceptionNowGenerated_FailsAsStale()
    {
        // 'x' is in the reference AND in the output: it is not among the missing codes, so only
        // the "now generated" check can catch the exception.
        var ex = Assert.ThrowsAny<XunitException>(
            () => GoldenDiffTests.AssertContainsReference("t", Set("a", "x"), Set("a", "x"), Exceptions("x")));
        Assert.Contains("stale: now generated", ex.Message, StringComparison.Ordinal);
        Assert.Contains("x", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyReference_Fails() =>
        Assert.ThrowsAny<XunitException>(() => GoldenDiffTests.AssertContainsReference("t", Set("a"), Set()));

    private sealed class CapturingOutput : ITestOutputHelper
    {
        public List<string> Lines { get; } = [];

        public void WriteLine(string message) => Lines.Add(message);

        public void WriteLine(string format, params object[] args) => Lines.Add(string.Format(format, args));
    }
}
