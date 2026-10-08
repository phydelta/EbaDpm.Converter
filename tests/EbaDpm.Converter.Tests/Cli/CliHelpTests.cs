using EbaDpm.Converter.Cli;

namespace EbaDpm.Converter.Tests.Cli;

/// <summary>
/// <c>--help</c>/<c>-h</c>/<c>-?</c>/<c>/?</c> print to STDOUT and return 0, even when combined
/// with other arguments (they are handled before anything else). With no arguments the previous
/// behaviour is kept: STDERR and exit code 1. That is not the "help" case but the "missing
/// arguments" case (the taxonomy filter is mandatory, there is no default mode).
/// </summary>
public sealed class CliHelpTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    public void HelpFlag_Alone_WritesToStdout_AndReturnsZero(string flag)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliRunner.Run([flag], stdout, stderr);

        Assert.Equal(CliRunner.ExitOk, exitCode);
        Assert.NotEmpty(stdout.ToString());
        Assert.Empty(stderr.ToString());
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    public void HelpFlag_CombinedWithOtherArguments_StillWritesToStdout_AndReturnsZero(string flag)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        // Deliberately incoherent arguments (no real source, no selector): if --help were not
        // handled first, this would fail because of missing/invalid arguments.
        var exitCode = CliRunner.Run(["--source", "does-not-exist.accdb", flag, "--taxonomies", "X"], stdout, stderr);

        Assert.Equal(CliRunner.ExitOk, exitCode);
        Assert.NotEmpty(stdout.ToString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public void NoArguments_StillWritesToStderr_AndReturnsArgumentError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliRunner.Run([], stdout, stderr);

        Assert.Equal(CliRunner.ExitArgumentError, exitCode);
        Assert.Empty(stdout.ToString());
        Assert.NotEmpty(stderr.ToString());
    }
}
