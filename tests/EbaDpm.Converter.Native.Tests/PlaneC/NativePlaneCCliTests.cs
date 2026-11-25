using System.Text.Json;
using EbaDpm.Converter.Cli;

namespace EbaDpm.Converter.Native.Tests.PlaneC;

/// <summary>
/// <c>--validate &lt;native&gt; --layouts &lt;repository&gt;</c> runs the native plane C (docs/usage.md,
/// docs/validation.md "Native output"): its checks are in the report, and a critical plane C failure
/// gives exit code 3. The native files are the synthetic ones of this folder (the real native schema,
/// a few rows), so plane A has nothing to fail on and the exit code is plane C's.
/// </summary>
public sealed class NativePlaneCCliTests : IDisposable
{
    private readonly string _scratch = Path.Join(Path.GetTempPath(), $"native-plc-cli_{Environment.ProcessId}_{Guid.NewGuid():N}");

    public NativePlaneCCliTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
            // Left for the OS temp cleanup.
        }
    }

    private static readonly string[] PlaneCIds =
        ["C-COB-01", "C-DPS-01", "C-DPS-02", "C-EQU-01", "C-EQU-02", "C-KEY-01", "C-MHR-01", "C-MHR-02"];

    private (int Exit, string Out, string Err, JsonDocument Report) Validate(string native, params string[] extra)
    {
        var report = Path.Join(_scratch, $"report_{Guid.NewGuid():N}.json");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliRunner.Run(["--validate", native, .. extra, "--report", report, "--plain"], stdout, stderr);
        var document = File.Exists(report) ? JsonDocument.Parse(File.ReadAllText(report)) : JsonDocument.Parse("{}");
        return (exit, stdout.ToString(), stderr.ToString(), document);
    }

    private static JsonElement Check(JsonDocument report, string id) =>
        report.RootElement.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);

    [Fact]
    public void Validate_WithLayouts_RunsPlaneC_AndAConformingOutputExitsZero()
    {
        using var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native);
        using var layouts = new SyntheticLayoutRepository();
        layouts.SignatureCell(layouts.Sheet("4.4", "T_01.00"), "MET(qAAA)", "qDIM(qM1)");

        var (exit, stdout, stderr, report) = Validate(native.FilePath, "--layouts", layouts.FilePath);

        Assert.True(exit == 0, $"exit {exit}; stderr: {stderr}; stdout: {stdout}");
        foreach (var id in PlaneCIds)
        {
            Assert.Equal("C", Check(report, id).GetProperty("plane").GetString());
        }

        var dps = Check(report, "C-DPS-01");
        Assert.Equal("pass", dps.GetProperty("status").GetString());
        Assert.Equal(1, dps.GetProperty("examined").GetInt64());
        Assert.Equal("native", report.RootElement.GetProperty("outputKind").GetString());
        Assert.StartsWith("plane C: evaluated against", report.RootElement.GetProperty("planeCNotice").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, report.RootElement.GetProperty("summary").GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void Validate_WithLayouts_ACriticalPlaneCFailureExitsThree_AndTheFailureIsInTheReport()
    {
        using var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native);
        PlaneCTestHelpers.Existing(native, "MEM", "qM2");
        using var layouts = new SyntheticLayoutRepository();
        layouts.SignatureCell(layouts.Sheet("4.4", "T_01.00"), "MET(qAAA)", "qDIM(qM2)"); // contradicts the native qM1.

        var (exit, stdout, _, report) = Validate(native.FilePath, "--layouts", layouts.FilePath);

        Assert.Equal(3, exit);
        var dps = Check(report, "C-DPS-01");
        Assert.Equal("fail", dps.GetProperty("status").GetString());
        Assert.Equal("critical", dps.GetProperty("layer").GetString());
        var sample = dps.GetProperty("samples").EnumerateArray().Single();
        Assert.Equal("4.4|T_01.00\tMET(qAAA)|qDIM(qM2)", sample.GetProperty("businessKey").GetString());
        Assert.Equal(3, report.RootElement.GetProperty("summary").GetProperty("exitCode").GetInt32());
        Assert.Contains("C-DPS-01", stdout);
    }

    [Fact]
    public void Validate_WithoutLayouts_PlaneCIsNotRun_AndTheReportSaysSo()
    {
        using var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native);

        var (exit, _, _, report) = Validate(native.FilePath);

        Assert.Equal(0, exit);
        Assert.Equal("plane C: not evaluated, --layouts missing.", report.RootElement.GetProperty("planeCNotice").GetString());
        var ids = report.RootElement.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToArray();
        Assert.DoesNotContain(ids, id => id!.StartsWith("C-", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WithALayoutRepositoryThatDoesNotExist_IsAnArgumentError()
    {
        using var native = new SyntheticNativeFile();

        var (exit, _, stderr, _) = Validate(native.FilePath, "--layouts", Path.Join(_scratch, "missing.db"));

        Assert.Equal(CliRunner.ExitArgumentError, exit);
        Assert.Contains("layout repository", stderr);
    }
}
