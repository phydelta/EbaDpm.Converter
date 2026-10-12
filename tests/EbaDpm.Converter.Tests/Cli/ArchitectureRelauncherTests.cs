using System.Runtime.InteropServices;
using EbaDpm.Converter.Cli;

namespace EbaDpm.Converter.Tests.Cli;

public sealed class ArchitectureRelauncherTests
{
    private static bool Should(bool cur = false, bool other = true, Architecture arch = Architecture.X64,
        bool already = false, bool sibling = true) =>
        ArchitectureRelauncher.ShouldRelaunch(cur, other, arch, already, sibling);

    [Fact]
    public void AllConditionsMet_Relaunches() => Assert.True(Should());

    [Theory]
    [InlineData("currentHasProvider")]
    [InlineData("otherMissing")]
    [InlineData("alreadyRelaunched")]
    [InlineData("noSibling")]
    [InlineData("x86Process")]
    [InlineData("arm64Process")]
    public void EachConditionFlipped_DoesNotRelaunch(string flipped)
    {
        // Positive control: the baseline relaunches; only the flipped condition differs.
        Assert.True(Should());
        var result = flipped switch
        {
            "currentHasProvider" => Should(cur: true),
            "otherMissing" => Should(other: false),
            "alreadyRelaunched" => Should(already: true),
            "noSibling" => Should(sibling: false),
            "x86Process" => Should(arch: Architecture.X86),
            "arm64Process" => Should(arch: Architecture.Arm64),
            _ => throw new ArgumentOutOfRangeException(nameof(flipped)),
        };
        Assert.False(result);
    }

    [Fact]
    public void SiblingPath_IsX86ExecutableUnderBaseDirectory()
    {
        var path = ArchitectureRelauncher.SiblingPath();
        Assert.EndsWith(Path.Join("x86", "EbaDpm.Converter.exe"), path, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(AppContext.BaseDirectory.TrimEnd('\\', '/'), path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RelaunchedVariable_IsDocumentedName() =>
        Assert.Equal("EBADPM_RELAUNCHED", ArchitectureRelauncher.RelaunchedVariable);
}
