using System.Data.OleDb;
using System.Security.Cryptography;
using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Native.Catalog;
using EbaDpm.Converter.Native.PlaneC;
using Microsoft.Data.Sqlite;
using FileInfo = EbaDpm.Converter.Core.Validation.FileInfo;

namespace EbaDpm.Converter.Native.Validation;

/// <summary>
/// <c>--validate</c> of a native DPM 2.0 output (docs/validation.md, "Native output"): plane A
/// (content against the source, relationships, completeness, schema, drift report), no plane B (there
/// is no reference export of this schema), and plane C with <c>--layouts</c>. The checks that need the
/// Access source (<c>--source</c>) are reported as skipped when there is none, never as passed. The
/// validated file is never modified: it is copied to a temporary file first, as for the 45-table output.
/// </summary>
public static class NativeValidator
{
    /// <summary>The value of <see cref="ValidationReport.OutputKind"/> of this validation.</summary>
    public const string OutputKind = "native";

    /// <param name="nativePath">The native output (SQLite).</param>
    /// <param name="accdbPath">The DPM 2.0 Access database it was converted from; optional.</param>
    /// <param name="layoutsPath">A layout repository (<c>--extract-layouts</c>); adds plane C.</param>
    public static ValidationReport Validate(string nativePath, string? accdbPath = null, string? layoutsPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativePath);
        if (!File.Exists(nativePath))
        {
            throw new FileNotFoundException($"file '{nativePath}' does not exist.", nativePath);
        }

        if (accdbPath is not null && !File.Exists(accdbPath))
        {
            throw new FileNotFoundException($"source file '{accdbPath}' does not exist.", accdbPath);
        }

        var generatedInfo = FileInfoOf(nativePath);
        var sourceInfo = accdbPath is null ? null : FileInfoOf(accdbPath);

        var tempCopyPath = Path.Join(Path.GetTempPath(), "EbaDpm.ValidateNative_" + Guid.NewGuid().ToString("N") + ".db");
        File.Copy(nativePath, tempCopyPath, overwrite: true);

        try
        {
            using var output = OpenReadOnly(tempCopyPath);
            var results = new List<CheckResult>();
            long tables;

            if (accdbPath is null)
            {
                results.AddRange(NativePlaneAChecks.SkippedChecks());
                results.Add(NativePlaneAChecks.ForeignKeys(output));
                results.Add(NativePlaneAChecks.Schema(output, NativeBaseline.Load().Catalog, fromSource: false));
                results.Add(NativePlaneAChecks.SkippedDrift());
            }
            else
            {
                using var access = new OleDbConnection(AceProvider.ConnectionString(accdbPath));
                access.Open();
                var catalog = NativeCatalogReader.Read(access);

                results.Add(NativePlaneAChecks.Completeness(output, catalog));
                results.Add(NativePlaneAChecks.RowCounts(output, access, catalog));
                results.Add(NativePlaneAChecks.Content(output, access, catalog));
                results.Add(NativePlaneAChecks.ForeignKeys(output));
                results.Add(NativePlaneAChecks.Schema(output, catalog, fromSource: true));
                results.Add(NativePlaneAChecks.Drift(NativeDrift.Compute(catalog)));
            }

            tables = NativeCheckSupport.ReadOutputTables(output).Count;

            IReadOnlyList<ExceptionInfo> exceptions = [];
            string planeCNotice;
            if (layoutsPath is null)
            {
                planeCNotice = "plane C: not evaluated, --layouts missing.";
            }
            else
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = layoutsPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString();
                using var layouts = new SqliteConnection(connectionString);
                layouts.Open();
                var planeC = NativePlaneC.Run(output, layouts);
                results.AddRange(planeC.Checks);
                exceptions = planeC.Divergences;
                planeCNotice = $"plane C: evaluated against '{Path.GetFileName(layoutsPath)}' (layouts repository).";
            }

            var checks = results.Select(ToCheckInfo).ToList();
            return new ValidationReport(
                SchemaVersion: 1,
                Tool: new ToolInfo("EbaDpm.Converter", typeof(NativeValidator).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"),
                GeneratedAt: DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Mode: "validate",
                Inputs: new InputsInfo(generatedInfo, null, sourceInfo),
                Universe: new UniverseInfo([], [], [], tables, 0, 0, null),
                Exceptions: exceptions,
                Checks: checks,
                Summary: Summarize(checks, exceptions),
                ExemplarNotice: null,
                PlaneCNotice: planeCNotice,
                OutputKind: OutputKind);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(tempCopyPath);
            TryDelete(tempCopyPath + "-shm");
            TryDelete(tempCopyPath + "-wal");
        }
    }

    /// <summary>Critical failures and stale exceptions of planes A and C give exit code 3 (there is no plane B).</summary>
    private static SummaryInfo Summarize(List<CheckInfo> checks, IReadOnlyList<ExceptionInfo> exceptions)
    {
        var critical = checks.Where(c => c.Layer == "critical").ToList();
        var informative = checks.Where(c => c.Layer == "informative").ToList();
        var criticalFailed = critical.Count(c => c.Status == "fail");
        var informativeFailed = informative.Count(c => c.Status is "fail" or "info");
        var stale = exceptions.Count(e => e.Stale);

        // A skipped check examined nothing: it is neither passed nor failed, and is counted apart.
        return new SummaryInfo(
            new SummaryBlock(critical.Count, critical.Count(c => c.Status == "pass"), criticalFailed, critical.Count(c => c.Status == "skipped")),
            new SummaryBlock(informative.Count, informative.Count(c => c.Status == "pass"), informativeFailed, informative.Count(c => c.Status == "skipped")),
            stale,
            criticalFailed > 0 || stale > 0 ? 3 : 0,
            PlaneBCriticalFailed: 0,
            PlaneBStaleExceptions: 0);
    }

    private static CheckInfo ToCheckInfo(CheckResult r) => new(
        r.Id,
        r.Plane switch { ValidationPlane.A => "A", ValidationPlane.B => "B", ValidationPlane.C => "C", _ => "?" },
        r.Layer == ValidationLayer.Critical ? "critical" : "informative",
        r.Table,
        r.Statement,
        r.Status switch
        {
            CheckStatus.Pass => "pass",
            CheckStatus.Fail => "fail",
            CheckStatus.Info => "info",
            CheckStatus.Skipped => "skipped",
            _ => "unknown",
        },
        r.Examined,
        r.Matched,
        r.Failed,
        r.ElapsedMs,
        r.Samples.Select(s => new SampleInfo(s.BusinessKey, s.Generated, s.Reference)).ToList(),
        r.TotalFailed is not null ? true : null,
        r.TotalFailed,
        r.SkipReason);

    private static FileInfo FileInfoOf(string path)
    {
        var size = new System.IO.FileInfo(path).Length;
        // Shared read: the file may be open elsewhere (a pooled ACE connection, or the user in Access).
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new FileInfo(path, size, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort: a temporary file that is still locked does not abort the validation.
        }
    }
}
