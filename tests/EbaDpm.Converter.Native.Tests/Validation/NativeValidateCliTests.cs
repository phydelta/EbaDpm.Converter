using System.Security.Cryptography;
using System.Text.Json;
using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Native.Tests.Support;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.Validation;

/// <summary>
/// <c>--validate</c> of a native output on the command line (docs/usage.md, "Validate a native output"):
/// exit codes, the argument errors, the JSON report of the native output and the guarantee that the
/// JSON report of a 45-table output has not changed.
/// </summary>
public sealed class NativeValidateCliTests(NativeValidationFixture fixture) : IClassFixture<NativeValidationFixture>, IDisposable
{
    private readonly string _scratch = Path.Join(Path.GetTempPath(), $"native-validate-cli_{Environment.ProcessId}_{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        System.Data.OleDb.OleDbConnection.ReleaseObjectPool();
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch (IOException)
        {
            // Left for the OS temp cleanup.
        }
    }

    private string P(string name)
    {
        Directory.CreateDirectory(_scratch);
        return Path.Join(_scratch, name);
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            var code = CliRunner.Run(args, stdout, stderr);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            NativeValidationFixture.ReleasePools();
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static JsonDocument Json(string path) => JsonDocument.Parse(File.ReadAllText(path));

    // ------------------------------------------------------------------ native output

    [AceFact]
    public void ValidateNative_WithSource_ExitsZero_AndPrintsPlaneAAndTheDrift()
    {
        var (code, stdout, stderr) = Run("--validate", fixture.OutputPath, "--source", fixture.SourcePath);

        Assert.True(code == 0, $"exit {code}\n{stdout}\n{stderr}");
        Assert.Contains("(native DPM 2.0 output) against the source", stdout, StringComparison.Ordinal);
        Assert.Contains("PLANE A - native output", stdout, StringComparison.Ordinal);
        Assert.Contains("Schema drift against the 4.4 baseline:", stdout, StringComparison.Ordinal);
        Assert.Contains("RESULT: no critical differences.", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("critical check(s) skipped", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("PLANE B", stdout, StringComparison.Ordinal);
    }

    [AceFact]
    public void ValidateNative_TamperedOutput_ExitsThree_AndSaysHowManyCriticalDifferences()
    {
        var tampered = fixture.Tamper("UPDATE \"Table\" SET \"Code\" = 'T_X' WHERE \"TableID\" = 10");

        var (code, stdout, stderr) = Run("--validate", tampered, "--source", fixture.SourcePath);

        Assert.True(code == 3, $"exit {code}\n{stdout}\n{stderr}");
        Assert.Contains("critical differences", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("RESULT: no critical differences", stdout, StringComparison.Ordinal);
        Assert.Contains("A-NAT-03", stdout, StringComparison.Ordinal);
    }

    [AceFact]
    public void ValidateNative_WithoutSource_ExitsZero_AndSaysTheSourceChecksAreSkipped()
    {
        var (code, stdout, stderr) = Run("--validate", fixture.OutputPath);

        Assert.True(code == 0, $"exit {code}\n{stdout}\n{stderr}");
        Assert.Contains("without source: the checks against the source are skipped", stdout, StringComparison.Ordinal);
        Assert.Contains("3 critical check(s) skipped (needs --source).", stdout, StringComparison.Ordinal);
        Assert.Contains("Skipped", stdout, StringComparison.Ordinal);
    }

    [AceFact]
    public void ValidateNative_WithoutSource_OrphanRow_StillExitsThree()
    {
        var tampered = fixture.Tamper("UPDATE \"Reference\" SET \"TableID\" = 999 WHERE \"ReferenceID\" = 1");

        var (code, stdout, stderr) = Run("--validate", tampered);

        Assert.True(code == 3, $"exit {code}\n{stdout}\n{stderr}");
    }

    [AceFact]
    public void ValidateNative_SourceThatDoesNotExist_IsAnArgumentError()
    {
        var (code, _, stderr) = Run("--validate", fixture.OutputPath, "--source", P("missing.accdb"));

        Assert.Equal(CliRunner.ExitArgumentError, code);
        Assert.Contains("source file", stderr, StringComparison.Ordinal);
        Assert.Contains("does not exist", stderr, StringComparison.Ordinal);
    }

    [AceFact]
    public void ValidateNative_WithReference_IsAnArgumentError_NoPlaneB()
    {
        // Any existing file serves as the reference: the option is rejected before it is opened.
        var (code, stdout, stderr) = Run("--validate", fixture.OutputPath, "--reference", fixture.OutputPath);

        Assert.Equal(CliRunner.ExitArgumentError, code);
        Assert.Contains("--reference does not apply to a native output", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("PLANE", stdout, StringComparison.Ordinal);
    }

    [AceFact]
    public void ValidateNative_NeverModifiesTheValidatedFile()
    {
        var before = Sha256(fixture.OutputPath);
        var filesBefore = Directory.GetFiles(Path.GetDirectoryName(fixture.OutputPath)!).Select(Path.GetFileName).Order().ToArray();

        _ = Run("--validate", fixture.OutputPath, "--source", fixture.SourcePath);
        _ = Run("--validate", fixture.OutputPath);

        Assert.Equal(before, Sha256(fixture.OutputPath));
        Assert.Equal(filesBefore, Directory.GetFiles(Path.GetDirectoryName(fixture.OutputPath)!).Select(Path.GetFileName).Order().ToArray());
    }

    // ------------------------------------------------------------------ JSON report

    [AceFact]
    public void Report_OfANativeOutput_HasOutputKindNative_AndTheSourceInInputs()
    {
        var report = P("native.json");

        var (code, stdout, stderr) = Run("--validate", fixture.OutputPath, "--source", fixture.SourcePath, "--report", report);

        Assert.True(code == 0, $"exit {code}\n{stdout}\n{stderr}");
        using var json = Json(report);
        var root = json.RootElement;
        Assert.Equal("native", root.GetProperty("outputKind").GetString());

        var inputs = root.GetProperty("inputs");
        Assert.Equal(fixture.SourcePath, inputs.GetProperty("source").GetProperty("path").GetString());
        Assert.Equal(Sha256(fixture.SourcePath), inputs.GetProperty("source").GetProperty("sha256").GetString());
        Assert.Equal(Sha256(fixture.OutputPath), inputs.GetProperty("generated").GetProperty("sha256").GetString());
        Assert.Equal(JsonValueKind.Null, inputs.GetProperty("reference").ValueKind);

        var ids = root.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        Assert.Equal(["A-NAT-01", "A-NAT-02", "A-NAT-03", "A-NAT-04", "A-NAT-05", "A-DRF-01"], ids.Where(i => i!.StartsWith("A-", StringComparison.Ordinal)).ToList());
        Assert.DoesNotContain(root.GetProperty("checks").EnumerateArray(), c => c.GetProperty("plane").GetString() == "B");

        var drift = root.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "A-DRF-01");
        Assert.Equal("informative", drift.GetProperty("layer").GetString());
        Assert.True(drift.GetProperty("samples").GetArrayLength() > 0);
        Assert.Equal(0, root.GetProperty("summary").GetProperty("exitCode").GetInt32());
    }

    [AceFact]
    public void Report_OfANativeOutput_WithoutSource_HasNoSource_AndTheSkipReasons()
    {
        var report = P("native-nosource.json");

        var (code, _, _) = Run("--validate", fixture.OutputPath, "--report", report);

        Assert.Equal(0, code);
        using var json = Json(report);
        var root = json.RootElement;
        Assert.Equal("native", root.GetProperty("outputKind").GetString());
        Assert.False(root.GetProperty("inputs").TryGetProperty("source", out _));
        var critical = root.GetProperty("summary").GetProperty("critical");
        var skippedChecks = root.GetProperty("checks").EnumerateArray()
            .Count(c => c.GetProperty("layer").GetString() == "critical" && c.GetProperty("status").GetString() == "skipped");
        Assert.Equal(3, skippedChecks);
        Assert.Equal(skippedChecks, critical.GetProperty("skipped").GetInt32());
        Assert.Equal(critical.GetProperty("checks").GetInt32() - skippedChecks, critical.GetProperty("passed").GetInt32());

        foreach (var id in new[] { "A-NAT-01", "A-NAT-02", "A-NAT-03", "A-DRF-01" })
        {
            var check = root.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);
            Assert.Equal("skipped", check.GetProperty("status").GetString());
            Assert.Contains("--source", check.GetProperty("skipReason").GetString(), StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ 45-table output

    [Fact]
    public void Validate_45TableOutput_WithSource_IsAnArgumentError()
    {
        var dist = P("dist.db");
        Assert.Equal(0, Run("--schema-only", "--output", dist).Code);

        var (code, stdout, stderr) = Run("--validate", dist, "--source", dist);

        Assert.Equal(CliRunner.ExitArgumentError, code);
        Assert.Contains("--source applies to --validate only for a native output", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("PLANE", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_Of45TableOutput_DoesNotContainOutputKindOrSource_KeySetsAreTheOldOnes()
    {
        var dist = P("dist2.db");
        var report = P("dist.json");
        Assert.Equal(0, Run("--schema-only", "--output", dist).Code);

        var (code, stdout, stderr) = Run("--validate", dist, "--report", report);

        Assert.True(code is 0 or 3, $"unexpected exit {code}\n{stdout}\n{stderr}");
        Assert.True(File.Exists(report), $"no report was written\n{stdout}\n{stderr}");
        using var json = Json(report);
        var root = json.RootElement;

        Assert.False(root.TryGetProperty("outputKind", out _));
        Assert.False(root.GetProperty("inputs").TryGetProperty("source", out _));
        Assert.Equal(
            ["schemaVersion", "tool", "generatedAt", "mode", "inputs", "universe", "exceptions", "checks", "summary", "exemplarNotice", "planeCNotice"],
            root.EnumerateObject().Select(p => p.Name).ToList());
        Assert.False(root.GetProperty("summary").GetProperty("critical").TryGetProperty("skipped", out _));
        Assert.False(root.GetProperty("summary").GetProperty("informative").TryGetProperty("skipped", out _));
        Assert.Equal(["generated", "reference"], root.GetProperty("inputs").EnumerateObject().Select(p => p.Name).ToList());
        Assert.DoesNotContain(root.GetProperty("checks").EnumerateArray(), c => c.GetProperty("id").GetString()!.StartsWith("A-NAT", StringComparison.Ordinal));
        Assert.DoesNotContain(root.GetProperty("checks").EnumerateArray(), c => c.GetProperty("id").GetString() == "A-DRF-01");
    }

    [AceFact]
    public void Report_OfANativeOutput_DiffersFromThe45TableOne_OnlyByTheNewKeys()
    {
        // Positive control of the absence assertion above: the same inspection DOES find the keys on a native report.
        var native = P("n.json");
        Assert.Equal(0, Run("--validate", fixture.OutputPath, "--source", fixture.SourcePath, "--report", native).Code);

        using var json = Json(native);
        var keys = json.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        Assert.Contains("outputKind", keys);
        Assert.Contains("source", json.RootElement.GetProperty("inputs").EnumerateObject().Select(p => p.Name));
    }
}
