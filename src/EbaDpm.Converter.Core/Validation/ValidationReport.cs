using System.Text.Json.Serialization;

namespace EbaDpm.Converter.Core.Validation;

/// <summary>Model of the <c>--validate</c> JSON report. Fixed schema, version 1.</summary>
public sealed record ValidationReport(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("tool")] ToolInfo Tool,
    [property: JsonPropertyName("generatedAt")] string GeneratedAt,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("inputs")] InputsInfo Inputs,
    [property: JsonPropertyName("universe")] UniverseInfo Universe,
    [property: JsonPropertyName("exceptions")] IReadOnlyList<ExceptionInfo> Exceptions,
    [property: JsonPropertyName("checks")] IReadOnlyList<CheckInfo> Checks,
    [property: JsonPropertyName("summary")] SummaryInfo Summary,
    [property: JsonPropertyName("exemplarNotice")] string? ExemplarNotice = null,
    // ALWAYS populated: "plane C: not evaluated, --layouts missing" when the layouts repository was
    // not supplied, never a silence (a plane that does not run and does not announce itself is a
    // hollow zero).
    [property: JsonPropertyName("planeCNotice")] string? PlaneCNotice = null);

public sealed record ToolInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);

public sealed record FileInfo(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record InputsInfo(
    [property: JsonPropertyName("generated")] FileInfo Generated,
    [property: JsonPropertyName("reference")] FileInfo? Reference);

public sealed record UniverseInfo(
    [property: JsonPropertyName("comparableTaxonomies")] IReadOnlyList<string> ComparableTaxonomies,
    [property: JsonPropertyName("generatedOnlyTaxonomies")] IReadOnlyList<string> GeneratedOnlyTaxonomies,
    [property: JsonPropertyName("referenceOnlyTaxonomies")] IReadOnlyList<string> ReferenceOnlyTaxonomies,
    [property: JsonPropertyName("tables")] long Tables,
    [property: JsonPropertyName("ordinates")] long Ordinates,
    [property: JsonPropertyName("cells")] long Cells,
    [property: JsonPropertyName("referenceRole")] string? ReferenceRole);

/// <param name="Evaluated">
/// <c>false</c> when NO check with <c>Id == Scope</c> ran in this execution: the exception is
/// registered but has nothing to attach to yet (e.g. <c>DD-17</c>, <c>DD-18</c>, <c>DD-20</c>,
/// <c>DD-22</c>). Different from <c>AppliesToThisReference=false</c> (the check DID run, but this
/// reference is not its own) and from <c>Matched=0</c> with <c>Evaluated=true</c> (it ran, it was
/// evaluated, it found nothing); the three cases would otherwise be indistinguishable in the report.
/// </param>
/// <param name="Plane">
/// The plane of <see cref="Scope"/> (the check that resolves this exception), ALWAYS resolved by
/// the declaration of that check (<c>KnownExceptions.PlaneOf</c>), never by parsing the id prefix.
/// <c>null</c> if the check did not run this session (same case as <see cref="Evaluated"/><c>=false</c>):
/// with no data, it is not guessed.
/// </param>
/// <param name="Reference">
/// The reference file that <see cref="KnownException"/> is bound to: <c>"*"</c> if it applies to
/// any, <c>"-"</c> if it belongs to plane A/C with no file to compare against (its declared
/// reference).
/// </param>
public sealed record ExceptionInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("businessKey")] string BusinessKey,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("plane")] string? Plane,
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("appliesToThisReference")] bool AppliesToThisReference,
    [property: JsonPropertyName("matched")] long Matched,
    [property: JsonPropertyName("stale")] bool Stale,
    [property: JsonPropertyName("evaluated")] bool Evaluated = true);

public sealed record SampleInfo(
    [property: JsonPropertyName("businessKey")] string BusinessKey,
    [property: JsonPropertyName("generated")] string? Generated,
    [property: JsonPropertyName("reference")] string? Reference);

public sealed record CheckInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("plane")] string Plane,
    [property: JsonPropertyName("layer")] string Layer,
    [property: JsonPropertyName("table")] string Table,
    [property: JsonPropertyName("statement")] string Statement,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("examined")] long Examined,
    [property: JsonPropertyName("matched")] long Matched,
    [property: JsonPropertyName("failed")] long Failed,
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs,
    [property: JsonPropertyName("samples")] IReadOnlyList<SampleInfo> Samples,
    [property: JsonPropertyName("truncated")] bool? Truncated,
    [property: JsonPropertyName("totalFailed")] long? TotalFailed,
    [property: JsonPropertyName("skipReason")] string? SkipReason);

public sealed record SummaryBlock(
    [property: JsonPropertyName("checks")] long Checks,
    [property: JsonPropertyName("passed")] long Passed,
    [property: JsonPropertyName("failed")] long Failed);

/// <param name="Critical">ALL critical checks, of the three planes.</param>
/// <param name="StaleExceptions">ALL expired exceptions, of the three planes.</param>
/// <param name="PlaneBCriticalFailed">
/// Of <see cref="Critical"/>, how many belong to PLANE B and are in <c>fail</c>. Plane B compares
/// against a third-party reference database that is informational and not authoritative, so these
/// are reported (they remain inside <see cref="Critical"/>) but do NOT feed <see cref="ExitCode"/>.
/// As a mandatory counterpart to no longer failing the run, they are counted separately, never
/// diluted.
/// </param>
/// <param name="PlaneBStaleExceptions">
/// Of <see cref="StaleExceptions"/>, how many resolve to a PLANE B check. They are still counted
/// (and named, see <c>ValidationReport.Exceptions</c>) but do not feed <see cref="ExitCode"/>
/// either.
/// </param>
public sealed record SummaryInfo(
    [property: JsonPropertyName("critical")] SummaryBlock Critical,
    [property: JsonPropertyName("informative")] SummaryBlock Informative,
    [property: JsonPropertyName("staleExceptions")] long StaleExceptions,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("planeBCriticalFailed")] long PlaneBCriticalFailed,
    [property: JsonPropertyName("planeBStaleExceptions")] long PlaneBStaleExceptions);
