namespace EbaDpm.Converter.Core.Validation;

/// <summary>Plane of a check.</summary>
public enum ValidationPlane
{
    /// <summary>Internal consistency: only needs the generated database. Always runs.</summary>
    A,

    /// <summary>Semantic diff against the reference database. Only with <c>--reference</c>.</summary>
    B,

    /// <summary>
    /// Signature-by-signature comparison against the EBA Annotated Table Layout. Only with
    /// <c>--layouts</c>; independent of <c>--reference</c>/plane B: the layout is a source of truth
    /// of its own, not a third-party reference.
    /// </summary>
    C,
}

/// <summary>Layer: critical makes the run fail (exit 3); informative is only reported.</summary>
public enum ValidationLayer
{
    Critical,
    Informative,
}

/// <summary>Final status of a check.</summary>
public enum CheckStatus
{
    Pass,
    Fail,

    /// <summary>There are differences, but the layer is informative: reported without failing the run.</summary>
    Info,

    /// <summary>The check does not apply in this universe (e.g. column absent from the reference).</summary>
    Skipped,
}

/// <summary>A concrete sample object, ALWAYS identified by business key, never by ID.</summary>
public sealed record CheckSample(string BusinessKey, string? Generated = null, string? Reference = null);

/// <summary>
/// Result of a single check (internal plane A invariant, or plane B comparison).
/// <paramref name="Samples"/> is capped at 50 objects; <paramref name="TotalFailed"/> carries the
/// real count when truncated.
/// </summary>
public sealed record CheckResult(
    string Id,
    ValidationPlane Plane,
    ValidationLayer Layer,
    string Table,
    string Statement,
    CheckStatus Status,
    long Examined,
    long Matched,
    long Failed,
    long ElapsedMs,
    IReadOnlyList<CheckSample> Samples,
    long? TotalFailed = null,
    string? SkipReason = null)
{
    public const int MaxSamples = 50;

    public static CheckResult FromViolationCount(
        string id,
        ValidationPlane plane,
        ValidationLayer layer,
        string table,
        string statement,
        long examined,
        long failed,
        long elapsedMs,
        IReadOnlyList<CheckSample> samples,
        long? totalFailed = null)
    {
        var status = failed == 0 ? CheckStatus.Pass : (layer == ValidationLayer.Critical ? CheckStatus.Fail : CheckStatus.Info);
        return new CheckResult(
            id, plane, layer, table, statement, status, examined, examined - failed, failed, elapsedMs,
            Truncate(samples), totalFailed ?? (failed > MaxSamples ? failed : null));
    }

    public static CheckResult Skip(string id, ValidationPlane plane, ValidationLayer layer, string table, string statement, string reason) =>
        new(id, plane, layer, table, statement, CheckStatus.Skipped, 0, 0, 0, 0, [], null, reason);

    private static IReadOnlyList<CheckSample> Truncate(IReadOnlyList<CheckSample> samples) =>
        samples.Count > MaxSamples ? samples.Take(MaxSamples).ToList() : samples;
}
