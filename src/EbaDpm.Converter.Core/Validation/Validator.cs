using System.Security.Cryptography;
using EbaDpm.Converter.Core.Validation.Checks;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation;

/// <summary>
/// Orchestrates the <c>--validate</c> mode: plane A always, plane B only with <c>--reference</c>,
/// plane C only with <c>--layouts</c>. Accumulates the report and decides the exit code.
/// </summary>
public static class Validator
{
    public sealed record Result(ValidationReport Report, int ExitCode);

    public static Result Run(string generatedPath, string? referencePath, string? layoutsPath = null)
    {
        KnownExceptions.ResetQueryTracking();
        var generatedInfo = FileInfoOf(generatedPath);

        // The 8 auxiliary indexes are created on a TEMPORARY COPY, never on the product output
        // (the output is a schema contract).
        var tempCopyPath = Path.Combine(Path.GetTempPath(), "EbaDpm.Validate_" + Guid.NewGuid().ToString("N") + ".db");
        File.Copy(generatedPath, tempCopyPath, overwrite: true);

        try
        {
            using var generated = OpenReadWrite(tempCopyPath);
            AuxiliaryIndexes.Create(generated);

            // The checking SCOPE comes from a row of aDatabaseProperties. Its absence means
            // DPM 1.0 (the DPM 1.0 pipeline does not write it), so DPM 1.0 output does not change
            // by a single byte.
            var sourceModel = ValidationSourceModelDetector.Detect(generated);

            var checks = new List<CheckInfo>();
            var exceptionOutcomes = new List<KnownExceptions.Outcome>();

            foreach (var result in RunPlaneA(generated, sourceModel))
            {
                checks.Add(ToCheckInfo(result));
            }

            UniverseInfo universe;
            FileInfo? referenceFileInfo = null;
            string? referenceRole = null;
            string? exemplarNotice = null;

            if (referencePath is null)
            {
                universe = new UniverseInfo([], [], [], 0, 0, 0, null);
            }
            else
            {
                referenceFileInfo = FileInfoOf(referencePath);
                referenceRole = ReferenceRole.Resolve(referencePath);
                exemplarNotice = ExemplarNotice(referenceRole, referenceFileInfo.Sha256);

                using var reference = OpenReadOnly(referencePath);
                var comparable = ComparableUniverseResolver.Resolve(generated, reference);

                if (comparable.Taxonomies.Count == 0)
                {
                    // An empty intersection is not a failure.
                    universe = new UniverseInfo([], comparable.GeneratedOnlyTaxonomies, comparable.ReferenceOnlyTaxonomies, 0, 0, 0, referenceRole);
                }
                else
                {
                    foreach (var result in RunPlaneB(generated, reference, referenceRole, comparable.Taxonomies, exceptionOutcomes, sourceModel))
                    {
                        checks.Add(ToCheckInfo(result));
                    }

                    var (tables, ordinates, cells) = UniverseCounts(generated, reference, comparable.Taxonomies);
                    universe = new UniverseInfo(
                        comparable.Taxonomies.OrderBy(t => t, StringComparer.Ordinal).ToList(),
                        comparable.GeneratedOnlyTaxonomies, comparable.ReferenceOnlyTaxonomies, tables, ordinates, cells, referenceRole);
                }
            }

            // Plane C is optional and independent of --reference. Without --layouts it does NOT
            // run and the report says so explicitly; there is no behavior change for DPM 1.0,
            // which never passes this option.
            string? planeCNotice;
            if (layoutsPath is null)
            {
                planeCNotice = "plane C: not evaluated, --layouts missing.";
            }
            else
            {
                using var layoutsConnection = OpenReadOnly(layoutsPath);
                foreach (var result in PlaneCChecks.Run(generated, layoutsConnection, exceptionOutcomes))
                {
                    checks.Add(ToCheckInfo(result));
                }

                planeCNotice = $"plane C: evaluated against '{Path.GetFileName(layoutsPath)}' (layouts repository).";
            }

            var exceptions = BuildExceptionReport(exceptionOutcomes, referenceRole);
            var summary = Summarize(checks, exceptions);

            var report = new ValidationReport(
                SchemaVersion: 1,
                Tool: new ToolInfo("EbaDpm.Converter", ToolVersion()),
                GeneratedAt: DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Mode: "validate",
                Inputs: new InputsInfo(generatedInfo, referenceFileInfo),
                Universe: universe,
                Exceptions: exceptions,
                Checks: checks,
                Summary: summary,
                ExemplarNotice: exemplarNotice,
                PlaneCNotice: planeCNotice);

            return new Result(report, summary.ExitCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(tempCopyPath);
            TryDelete(tempCopyPath + "-shm");
            TryDelete(tempCopyPath + "-wal");
        }
    }

    private static IEnumerable<CheckResult> RunPlaneA(SqliteConnection c, ValidationSourceModel model) =>
        IntegrityChecks.Run(c, model)
            .Concat(DictionaryChecks.Run(c, model))
            .Concat(TemplateChecks.Run(c))
            .Concat(AxisChecks.Run(c))
            .Concat(CellChecks.Run(c, model))
            .Concat(ModuleChecks.Run(c))
            .Concat(ConceptChecks.Run(c, model))
            .Concat(EmptyTableChecks.Run(c, model));

    private static IEnumerable<CheckResult> RunPlaneB(
        SqliteConnection generated, SqliteConnection reference, string referenceRole, IReadOnlySet<string> taxonomies,
        List<KnownExceptions.Outcome> exceptionSink, ValidationSourceModel model)
    {
        yield return SchemaCensusChecks.Run(generated, reference, referenceRole);

        foreach (var result in DictionaryCensusChecks.Run(generated, reference, referenceRole, exceptionSink))
        {
            yield return result;
        }

        foreach (var result in TemplateCensusChecks.Run(generated, reference, referenceRole, taxonomies, exceptionSink))
        {
            yield return result;
        }

        foreach (var result in AxisAndCellCensusChecks.Run(generated, reference, referenceRole, taxonomies, exceptionSink, model))
        {
            yield return result;
        }

        foreach (var result in ModuleCensusChecks.Run(generated, reference, referenceRole, taxonomies, exceptionSink))
        {
            yield return result;
        }
    }

    private static (long Tables, long Ordinates, long Cells) UniverseCounts(SqliteConnection generated, SqliteConnection reference, IReadOnlySet<string> taxonomies)
    {
        var tableIdsGenerated = BusinessKeys.BuildTableIdsByBusinessKey(generated, taxonomies);
        var tableIdsReference = BusinessKeys.BuildTableIdsByBusinessKey(reference, taxonomies);
        var tables = tableIdsGenerated.Keys.Intersect(tableIdsReference.Keys).Count();

        var referencePaths = BusinessKeys.BuildClosedOrdinatePathIndex(reference, tableIdsReference.Values.ToHashSet());
        var ordinates = referencePaths.Count;

        var referenceCells = BusinessKeys.BuildClosedCellPositionIndex(reference, tableIdsReference.Values.ToHashSet());
        var cells = referenceCells.Count;

        return (tables, ordinates, cells);
    }

    private static IReadOnlyList<ExceptionInfo> BuildExceptionReport(List<KnownExceptions.Outcome> outcomes, string? referenceRole)
    {
        var byKey = new Dictionary<(string Id, string BusinessKey, string Reference, string Check), KnownExceptions.Outcome>();
        foreach (var outcome in outcomes)
        {
            byKey[(outcome.Exception.Id, outcome.Exception.BusinessKey, outcome.Exception.Reference, outcome.Exception.Check)] = outcome;
        }

        var result = new List<ExceptionInfo>();
        foreach (var exception in KnownExceptions.All)
        {
            // An exception is EVALUATED if some check called KnownExceptions.For(exception.Check)
            // this session, regardless of whether it had anything to say about this specific key.
            // Do NOT compare against CheckResult.Id: the exception's Check is DELIBERATELY finer
            // than the CheckResult id (each exception is bound to the EXACT Check of the
            // sub-census, never to "B-DIC-01" as a whole), so they are different strings even when
            // a real check does exist behind it. If the Check does not even exist yet (DD-17,
            // DD-18, DD-20, DD-22), NOT EVALUATED is the truth, and without this field it was
            // indistinguishable from "evaluated and found nothing".
            var evaluated = KnownExceptions.WasQueried(exception.Check);
            // The plane is resolved from the check's declaration (never from the id prefix) -
            // null if the check did not run this session, same as Evaluated=false.
            var plane = PlaneText(KnownExceptions.PlaneOf(exception.Check));
            var key = (exception.Id, exception.BusinessKey, exception.Reference, exception.Check);
            if (byKey.TryGetValue(key, out var outcome))
            {
                result.Add(new ExceptionInfo(
                    exception.Id, KindText(exception.Kind), exception.BusinessKey, exception.Check, plane, exception.Reference, exception.Reason,
                    outcome.AppliesToThisReference, outcome.Matched, outcome.Stale, evaluated));
            }
            else
            {
                // Not evaluated: there was no --reference, or this exception requires another
                // reference and its associated check did not even run (e.g. empty comparable
                // universe), or the Check does not exist yet at all (evaluated=false tells this
                // case apart).
                var applies = referenceRole is not null && (exception.Reference == "*" || exception.Reference == referenceRole);
                result.Add(new ExceptionInfo(exception.Id, KindText(exception.Kind), exception.BusinessKey, exception.Check, plane, exception.Reference, exception.Reason, applies, 0, false, evaluated));
            }
        }

        return result;
    }

    /// <summary>Same mapping that <see cref="ToCheckInfo"/> uses for <c>CheckResult.Plane</c>: a single place, for exceptions and checks alike.</summary>
    private static string? PlaneText(ValidationPlane? plane) => plane switch
    {
        ValidationPlane.A => "A",
        ValidationPlane.B => "B",
        ValidationPlane.C => "C",
        _ => null,
    };

    private static string KindText(ExceptionKind kind) => kind switch
    {
        ExceptionKind.Defect => "defect",
        ExceptionKind.DeclaredDivergence => "declaredDivergence",
        ExceptionKind.OriginAnomaly => "originAnomaly",
        _ => "unknown",
    };

    /// <summary>
    /// What fails the conversion is decided by the PLANE, not only by the LAYER. Planes A and C
    /// (internal invariants and the EBA layout) fail as always; plane B (the comparison against a
    /// third-party reference SQLite export, which is informational and has no authority over
    /// whether the conversion is valid) keeps running and reporting with <c>status=fail</c>, but
    /// neither its failed critical checks nor its expired exceptions feed
    /// <see cref="SummaryInfo.ExitCode"/>. The mandatory counterpart: what stops failing the run is
    /// counted SEPARATELY, never diluted into the total.
    /// </summary>
    private static SummaryInfo Summarize(List<CheckInfo> checks, IReadOnlyList<ExceptionInfo> exceptions)
    {
        var critical = checks.Where(c => c.Layer == "critical").ToList();
        var informative = checks.Where(c => c.Layer == "informative").ToList();

        var criticalFailed = critical.Count(c => c.Status == "fail");
        var informativeFailed = informative.Count(c => c.Status is "fail" or "info");

        var staleExceptions = exceptions.Count(e => e.Stale);

        // An expired exception (stale/unexpectedMatch/unexpectedDivergence) is ALWAYS a critical
        // failure, but "always" is limited to planes A/C: that rule dates from when plane B was the
        // gate and speaks of LAYER, not of PLANE. A null plane (check not evaluated) is treated
        // conservatively: "B" is NOT assumed, it fails like A/C.
        var criticalFailedGating = critical.Count(c => c.Plane != "B" && c.Status == "fail");
        var staleExceptionsGating = exceptions.Count(e => e.Stale && e.Plane != "B");

        var planeBCriticalFailed = critical.Count(c => c.Plane == "B" && c.Status == "fail");
        var planeBStaleExceptions = exceptions.Count(e => e.Stale && e.Plane == "B");

        var exitCode = criticalFailedGating > 0 || staleExceptionsGating > 0 ? 3 : CliExitOk;

        return new SummaryInfo(
            new SummaryBlock(critical.Count, critical.Count - criticalFailed, criticalFailed),
            new SummaryBlock(informative.Count, informative.Count - informativeFailed, informativeFailed),
            staleExceptions,
            exitCode,
            planeBCriticalFailed,
            planeBStaleExceptions);
    }

    private const int CliExitOk = 0;

    private static CheckInfo ToCheckInfo(CheckResult r) => new(
        r.Id,
        r.Plane switch { ValidationPlane.A => "A", ValidationPlane.B => "B", ValidationPlane.C => "C", _ => "?" },
        r.Layer == ValidationLayer.Critical ? "critical" : "informative",
        r.Table,
        r.Statement,
        StatusText(r.Status),
        r.Examined,
        r.Matched,
        r.Failed,
        r.ElapsedMs,
        r.Samples.Select(s => new SampleInfo(s.BusinessKey, s.Generated, s.Reference)).ToList(),
        r.TotalFailed is not null ? true : null,
        r.TotalFailed,
        r.SkipReason);

    private static string StatusText(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "pass",
        CheckStatus.Fail => "fail",
        CheckStatus.Info => "info",
        CheckStatus.Skipped => "skipped",
        _ => "unknown",
    };

    private static FileInfo FileInfoOf(string path)
    {
        var size = new System.IO.FileInfo(path).Length;
        using var stream = System.IO.File.OpenRead(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return new FileInfo(path, size, hash);
    }

    private static SqliteConnection OpenReadWrite(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static string ToolVersion() =>
        typeof(Validator).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// The hash is DATA, not a gate. It is compared ONCE, at report level, never per exception
    /// (hundreds of identical warnings would be noise, not signal). Different from the publication
    /// guard (<c>DictionaryCensusChecks</c>): that one DOES skip because comparing across different
    /// publications is structurally invalid; this one is only UNCERTAIN, so the exception is still
    /// applied and a single warning is emitted.
    /// </summary>
    private static string? ExemplarNotice(string referenceRole, string actualSha256)
    {
        var measuredHashes = KnownExceptions.All
            .Where(e => e.Reference == referenceRole && e.ReferenceSha256 is not null)
            .Select(e => e.ReferenceSha256!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (measuredHashes.Count != 1 || string.Equals(measuredHashes[0], actualSha256, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"The exceptions of '{referenceRole}' were measured on copy {measuredHashes[0]}; " +
               $"you are validating against {actualSha256}. Results are PROVISIONAL: " +
               "same file name, possibly a different copy. The exceptions are APPLIED anyway; this is a warning, not a skip.";
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
            // Best effort: if the temporary file is still locked, validation is not aborted for that.
        }
    }
}
