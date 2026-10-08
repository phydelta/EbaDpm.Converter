using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.Checks;

/// <summary>
/// Utilities shared by the plane B checks: coverage by containment (the generated output
/// contains the reference) with the exception registry applied, and literal comparison with
/// declared divergences.
/// </summary>
public static class PlaneBSupport
{
    public static HashSet<string> Codes(SqliteConnection connection, string sql) =>
        SqlHelpers.Rows(connection, sql, 1)
            .Select(r => r[0])
            .Where(v => v is not null)
            .Select(v => v!)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Coverage by containment: the GENERATED output must contain the REFERENCE. An object of the
    /// reference that is absent from the generated output is a failure, unless a named exception
    /// covers it. An extra object in the generated output is informative, never a failure.
    /// </summary>
    public static CheckResult Containment(
        string id,
        ValidationLayer layer,
        string table,
        string statement,
        string exceptionCheckId,
        string referenceRole,
        IReadOnlySet<string> generated,
        IReadOnlySet<string> reference,
        List<KnownExceptions.Outcome> exceptionSink,
        long elapsedMs)
    {
        var missing = reference.Except(generated, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var (unresolvedMissing, outcomes) = KnownExceptions.ApplyContainmentExceptions(exceptionCheckId, referenceRole, missing, reference, ValidationPlane.B);
        exceptionSink.AddRange(outcomes);

        var samples = unresolvedMissing.Take(CheckResult.MaxSamples).Select(k => new CheckSample(k)).ToList();
        return CheckResult.FromViolationCount(
            id, ValidationPlane.B, layer, table, statement, reference.Count, unresolvedMissing.Count, elapsedMs, samples,
            unresolvedMissing.Count > CheckResult.MaxSamples ? unresolvedMissing.Count : null);
    }

    /// <summary>
    /// Literal comparison with declared divergences: every key common to both sides must match,
    /// except for a named divergence. A key absent from the generated output is reported
    /// separately (coverage), not here.
    /// </summary>
    public static CheckResult LiteralWithDeclaredDivergences(
        string id,
        ValidationLayer layer,
        string table,
        string statement,
        string exceptionCheckId,
        string referenceRole,
        IReadOnlyDictionary<string, (string Generated, string Reference)> comparableByKey,
        List<KnownExceptions.Outcome> exceptionSink,
        long elapsedMs)
    {
        var (unexpectedDivergences, outcomes) = KnownExceptions.ApplyDivergenceExceptions(exceptionCheckId, referenceRole, comparableByKey, ValidationPlane.B);
        exceptionSink.AddRange(outcomes);

        var samples = unexpectedDivergences.Take(CheckResult.MaxSamples)
            .Select(k => new CheckSample(k, comparableByKey[k].Generated, comparableByKey[k].Reference))
            .ToList();
        return CheckResult.FromViolationCount(
            id, ValidationPlane.B, layer, table, statement, comparableByKey.Count, unexpectedDivergences.Count, elapsedMs, samples,
            unexpectedDivergences.Count > CheckResult.MaxSamples ? unexpectedDivergences.Count : null);
    }
}
