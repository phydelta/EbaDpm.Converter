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

    /// <summary>
    /// The 4.2 reference export <c>EBA_4.2_Hotfix.db</c> (information only, never an authority): a
    /// figure pinned here is a measurement of that file, not a property of the format.
    /// </summary>
    public static readonly ReleasePin Reference42 = new(
        Schema.RepoPaths.Cutoff42ReleaseCode, Schema.RepoPaths.ReferenceDatabaseFileName, "2026-10-22");

    // ------------------------------------------------------------------
    // Censuses of the OUTPUT of the DPM 2.0 axis, cell and signature mapping rules, scope
    // Dpm20Cutoff42. Deriving them from the Access source would re-implement the rules under test
    // (table closure, abstract pruning, transposition, one dimension one axis), so they are pinned.
    // The identities that hold between them are asserted separately, as a second measurement.
    // ------------------------------------------------------------------

    /// <summary><c>COUNT(1)</c> of <c>mAxis</c> (and of <c>mTableAxis</c>: one link per axis).</summary>
    public const int Dpm20Cutoff42MAxisRows = 1993;

    /// <summary><c>COUNT(1)</c> of <c>mAxisOrdinate</c>.</summary>
    public const int Dpm20Cutoff42MAxisOrdinateRows = 20712;

    /// <summary><c>COUNT(1)</c> of <c>mTableCell</c>.</summary>
    public const int Dpm20Cutoff42MTableCellRows = 163218;

    /// <summary><c>COUNT(1)</c> of <c>mCellPosition</c>.</summary>
    public const int Dpm20Cutoff42MCellPositionRows = 415586;

    /// <summary>Closed X axes of <c>mAxis</c> (identity: one per <c>mTable</c> row).</summary>
    public const int Dpm20Cutoff42ClosedXAxes = 847;

    /// <summary>Closed Y axes of <c>mAxis</c>.</summary>
    public const int Dpm20Cutoff42ClosedYAxes = 719;

    /// <summary>Open Y axes of <c>mAxis</c> (key headers of direction X, transposed).</summary>
    public const int Dpm20Cutoff42OpenYAxes = 241;

    /// <summary>Closed Z axes of <c>mAxis</c>.</summary>
    public const int Dpm20Cutoff42ClosedZAxes = 38;

    /// <summary>Open Z axes of <c>mAxis</c> (key headers of direction Z).</summary>
    public const int Dpm20Cutoff42OpenZAxes = 148;

    /// <summary>Rows of <c>mOrdinateCategorisation</c> whose dimension is the metric dimension 9999.</summary>
    public const int Dpm20Cutoff42MetCategorisationRows = 9123;

    /// <summary>Cells whose <c>DPS</c> differs from <c>DatapointSignature</c> (the restricted open-axis bracket).</summary>
    public const int Dpm20Cutoff42BracketCells = 28065;

    /// <summary><c>COUNT(1)</c> of <c>mOpenAxisValueRestriction</c>.</summary>
    public const int Dpm20Cutoff42OpenAxisValueRestrictionRows = 207;

    /// <summary>
    /// Distinct (TableCode, AxisCode, HierarchyCode) keys of <c>mOpenAxisValueRestriction</c> in the
    /// 4.2 reference export (<see cref="Reference42"/>).
    /// </summary>
    public const int Reference42OpenAxisRestrictionKeys = 199;

    /// <summary>Asserts a pinned figure; the failure message prints the scope of the pin.</summary>
    public static void AssertPinned(ReleasePin pin, string what, long expected, long actual)
        => Xunit.Assert.True(expected == actual, Message(pin, what, expected, actual));

    /// <summary>Failure message: what was compared plus the scope of the pin.</summary>
    public static string Message(ReleasePin pin, string what, object expected, object actual)
        => $"{what}: expected {expected}, actual {actual} ({pin}).";
}
