namespace EbaDpm.Converter.Tests.Infrastructure;

/// <summary>
/// The scope of a figure that was measured on one release and cannot be derived independently
/// without re-implementing the rule under test. No such figure is a property of the format: it
/// holds for this release of this database, on the date it was measured, and a failure message
/// prints that scope (<see cref="ToString"/>) so a change of release is read as a re-measure, not
/// as a defect by default.
/// </summary>
/// <param name="Release">The <c>Release.Code</c> used as cutoff when the figure was measured.</param>
/// <param name="Database">The file name of the database the figure was measured on.</param>
/// <param name="MeasuredOn">The date (<c>yyyy-MM-dd</c>) of the measurement.</param>
internal sealed record ReleasePin(string Release, string Database, string MeasuredOn)
{
    public override string ToString() => $"pinned to release {Release} of '{Database}', measured {MeasuredOn}";
}

/// <summary>
/// Named, scoped pins. Add a constant here (never a bare literal in a test) and use
/// <see cref="Message"/> in the assertion. Prefer deriving the value with
/// <see cref="AccessCensus"/> whenever a rule-free query can compute it.
/// </summary>
internal static class ReleasePins
{
    /// <summary>The DPM 2.0 database of the 4.2.1 publication read with cutoff release 4.2.</summary>
    public static readonly ReleasePin Dpm20Cutoff42 = new(
        Schema.RepoPaths.Cutoff42ReleaseCode, Schema.RepoPaths.AccessDpm20DatabaseFileName, "2026-10-22");

    /// <summary>
    /// Distinct frameworks that carry at least one derived taxonomy (<c>Dpm20TaxonomyDeriver</c>).
    /// Not derivable without re-implementing the derivation (table closure, abstract tables, DORA).
    /// </summary>
    public const int Dpm20Cutoff42FrameworksWithTaxonomies = 18;

    /// <summary>Failure message: what was compared plus the scope of the pin.</summary>
    public static string Message(ReleasePin pin, string what, object expected, object actual)
        => $"{what}: expected {expected}, actual {actual} ({pin}).";
}
