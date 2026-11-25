using System.Data.OleDb;
using System.Diagnostics;
using EbaDpm.Converter.Native.Tests.Support;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.Cli;

/// <summary>
/// Runs the REAL executable as a child process. Once the ACE provider has served a connection, the
/// native teardown at process exit can replace the exit code with a crash code (issue #82, and a
/// segmentation fault seen at exit of a program that only read the Access catalogue). These tests
/// assert that the exit code is the computed one (0 or 1), never a crash code.
/// </summary>
public sealed class NativeCliProcessTests : IDisposable
{
    private readonly string _scratch = Path.Join(Path.GetTempPath(), $"native-proc_{Environment.ProcessId}_{Guid.NewGuid():N}");

    public NativeCliProcessTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TempFiles.ReleaseAccess();
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
            // Left for the OS temp cleanup.
        }
    }

    private static string Executable => Path.Join(AppContext.BaseDirectory, "EbaDpm.Converter.exe");

    private static (int Code, string Out, string Err) RunProcess(params string[] args)
    {
        Assert.True(File.Exists(Executable), $"The CLI executable was not found next to the tests: {Executable}");
        var start = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The CLI did not exit within 180 s.");
        }

        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string Show(int code) => $"{code} (0x{code:X8})";

    [AceFact]
    public void Process_PreBaselineSource_ExitsWithCodeOne_NotACrashCode_Repeatedly()
    {
        using var source = SyntheticDpm2Accdb.Create(["4.2.1", "4.3"]);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var output = Path.Join(_scratch, $"pre{attempt}.db");

            var (code, _, stderr) = RunProcess("--source", source.Path, "--output", output, "--target", "native", "--plain");

            Assert.True(code == 1, $"Attempt {attempt}: expected exit code 1, got {Show(code)}. stderr: {stderr}");
            Assert.Contains("4.4", stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
            Assert.False(File.Exists(output + ".partial"));
        }
    }

    [AceFact]
    public void Process_Dpm10Source_ExitsWithCodeOne_NotACrashCode()
    {
        var path = Dpm2TestAccdbFactory.CreateAccdbWithTables("Domain", "Category");
        try
        {
            var output = Path.Join(_scratch, "dpm1.db");

            var (code, _, stderr) = RunProcess("--source", path, "--output", output, "--target", "native", "--plain");

            Assert.True(code == 1, $"Expected exit code 1, got {Show(code)}. stderr: {stderr}");
            Assert.Contains("DPM 2.0", stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
        }
        finally
        {
            TempFiles.TryDeleteAccdb(path);
        }
    }

    [AceFact]
    public void Process_HappyPath_ExitsWithCodeZero_AndAValidFile()
    {
        using var source = SyntheticDpm2Accdb.Create();
        var output = Path.Join(_scratch, "ok.db");

        var (code, stdout, stderr) = RunProcess("--source", source.Path, "--output", output, "--target", "native", "--plain");

        Assert.True(code == 0, $"Expected exit code 0, got {Show(code)}. stderr: {stderr}");
        Assert.Contains("Native DPM 2.0 output written", stdout, StringComparison.Ordinal);
        using var sqlite = AccessSqliteComparer.OpenSqlite(output);
        Assert.Equal(9, AccessSqliteComparer.SqliteUserTables(sqlite).Count);
    }

    [Fact]
    public void Process_ArgumentError_ExitsWithCodeOne()
    {
        var (code, _, stderr) = RunProcess("--schema-only", "--output", Path.Join(_scratch, "bad.db"), "--target", "oracle", "--plain");

        Assert.True(code == 1, $"Expected exit code 1, got {Show(code)}. stderr: {stderr}");
        Assert.Contains("invalid value 'oracle' for --target", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_SchemaOnlyNative_ExitsWithCodeZero_AndWritesTheBaseline()
    {
        var output = Path.Join(_scratch, "schema.db");

        var (code, stdout, stderr) = RunProcess("--schema-only", "--output", output, "--target", "native", "--plain");

        Assert.True(code == 0, $"Expected exit code 0, got {Show(code)}. stderr: {stderr}");
        Assert.Contains("59 tables", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void ExitCodeCheck_PositiveControl_TheHarnessSeesANonZeroExitCode()
    {
        // The same harness reports 1 for a failing command line and 0 for a good one: the
        // assertions above can fail, they are not satisfied by any exit code.
        var (bad, _, _) = RunProcess("--schema-only", "--output", Path.Join(_scratch, "c1.db"), "--target", "oracle", "--plain");
        var (good, _, _) = RunProcess("--schema-only", "--output", Path.Join(_scratch, "c2.db"), "--plain");

        Assert.Equal(1, bad);
        Assert.Equal(0, good);
    }
}
