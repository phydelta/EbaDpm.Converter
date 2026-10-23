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
    /// The 4.2 Annotated Table Layouts (<see cref="Schema.RepoPaths.TableLayouts42DirectoryName"/>)
    /// compared by plane C with the <see cref="Dpm20Cutoff42"/> output. The figures of the plane C
    /// checks (units examined) depend on the layouts as much as on the output.
    /// </summary>
    public static readonly ReleasePin Layouts42 = new(
        Schema.RepoPaths.Cutoff42ReleaseCode,
        $"{Schema.RepoPaths.AccessDpm20DatabaseFileName} output against the layouts in '{Schema.RepoPaths.TableLayouts42DirectoryName}'",
        "2026-10-23");

    /// <summary>The DPM 2.0 database of the 4.2.1 publication read with its natural cutoff, 4.2.1.</summary>
    public static readonly ReleasePin Dpm20Natural421 = new(
        "4.2.1", Schema.RepoPaths.AccessDpm20DatabaseFileName, "2026-10-23");

    /// <summary>The DPM 2.0 database of the 4.3 publication read with its natural cutoff, 4.3.</summary>
    public static readonly ReleasePin Dpm20Natural43 = new(
        "4.3", Schema.RepoPaths.AccessDpm20Release43DatabaseFileName, "2026-10-23");

    /// <summary>The DPM 1.0 database of release 4.1 converted with <c>--all</c>.</summary>
    public static readonly ReleasePin Dpm10All41 = new(
        "4.1", Schema.RepoPaths.AccessDatabaseFileName, "2026-10-23");

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

    // ------------------------------------------------------------------
    // DPM 2.0 dictionary (mMetric, mDimension, mHierarchyNode and the comparison against the
    // 4.2 reference export). Metric/dimension identification and the hierarchy node population are
    // rules, so they are pinned rather than derived.
    // ------------------------------------------------------------------

    /// <summary><c>COUNT(1)</c> of <c>mMetric</c>.</summary>
    public const int Dpm20Cutoff42MMetricRows = 2107;

    /// <summary><c>COUNT(1)</c> of <c>mDimension</c> (includes the <c>MET</c> dimension, <c>DimensionID</c> 9999).</summary>
    public const int Dpm20Cutoff42MDimensionRows = 1100;

    /// <summary>Metrics of <c>mMetric</c> with a non-NULL <c>ReferencedHierarchyID</c>.</summary>
    public const int Dpm20Cutoff42MetricsWithHierarchy = 427;

    /// <summary>Distinct codes present in both <c>mMetric</c> (member code) and <c>mDimension</c>.</summary>
    public const int Dpm20Cutoff42CodesInBothMetricAndDimension = 45;

    /// <summary><c>COUNT(1)</c> of <c>mHierarchyNode</c> (and of its <c>HierarchyNode</c> concepts).</summary>
    public const int Dpm20Cutoff42MHierarchyNodeRows = 16538;

    /// <summary>Distinct <c>MemberCode</c> values of the <c>MET</c> domain in the 4.2 reference export.</summary>
    public const int Reference42MetMemberCodes = 2104;

    /// <summary>Hierarchies of the <c>MET</c> domain in the 4.2 reference export.</summary>
    public const int Reference42MetHierarchies = 21;

    /// <summary>Dimension codes present in both the generated <c>mDimension</c> and the 4.2 reference export.</summary>
    public const int Reference42DimensionCommonCodes = 1089;

    /// <summary>
    /// (domain, hierarchy code) keys present, with nodes, in both the generated output and the 4.2
    /// reference export.
    /// </summary>
    public const int Reference42HierarchyCommonKeys = 993;

    /// <summary>Common hierarchy keys whose member code sets are exactly equal to the reference's.</summary>
    public const int Reference42HierarchyExactKeys = 932;

    /// <summary>Common hierarchy keys whose member code sets are contained one in the other, not equal.</summary>
    public const int Reference42HierarchyContainedKeys = 55;

    // ------------------------------------------------------------------
    // DPM 2.0 modules and concepts. The business template links and the taxonomy/template
    // populations are the output of mapping rules (table closure, taxonomy derivation), so they are
    // pinned; module and concept counts that follow from the source or from a 1:1 mirror are derived.
    // ------------------------------------------------------------------

    /// <summary><c>COUNT(1)</c> of <c>mModuleBusinessTemplate</c>.</summary>
    public const int Dpm20Cutoff42MModuleBusinessTemplateRows = 127;

    /// <summary>Distinct <c>BusinessTemplateID</c> nodes referenced by <c>mModuleBusinessTemplate</c>.</summary>
    public const int Dpm20Cutoff42BusinessTemplateDistinctNodes = 111;

    /// <summary><c>COUNT(1)</c> of <c>mTaxonomy</c> (derived taxonomies, <c>Dpm20TaxonomyDeriver</c>).</summary>
    public const int Dpm20Cutoff42MTaxonomyRows = 32;

    /// <summary><c>COUNT(1)</c> of <c>mTemplateOrTable</c> (and of its <c>TemplateOrTable</c> concepts).</summary>
    public const int Dpm20Cutoff42MTemplateOrTableRows = 1530;

    /// <summary><c>COUNT(1)</c> of <c>mConceptTranslation</c>.</summary>
    public const int Dpm20Cutoff42MConceptTranslationRows = 57073;

    /// <summary><c>COUNT(1)</c> of <c>mModule</c> in the 4.2 reference export (<see cref="Reference42"/>).</summary>
    public const int Reference42MModuleRows = 50;

    // ------------------------------------------------------------------
    // DPM 2.0 structure (mTable, mTemplateOrTable tree) and taxonomy derivation, scope
    // Dpm20Cutoff42. Table closure, abstract resolution and the template tree are rules, so the
    // populations are pinned; the identities between them are asserted separately.
    // ------------------------------------------------------------------

    /// <summary><c>COUNT(1)</c> of <c>mTable</c> (and of <c>mTaxonomyTable</c> and of the <c>BusinessTable</c> rows of <c>mTemplateOrTable</c>).</summary>
    public const int Dpm20Cutoff42MTableRows = 847;

    /// <summary><c>TableGroup</c> rows of <c>mTemplateOrTable</c> with <c>Level</c> 1 (the root groups).</summary>
    public const int Dpm20Cutoff42TableGroupLevel1Rows = 111;

    /// <summary><c>TableGroup</c> rows of <c>mTemplateOrTable</c> with <c>Level</c> 2 (the templates).</summary>
    public const int Dpm20Cutoff42TableGroupLevel2Rows = 572;

    /// <summary>Distinct <c>TemplateOrTableCode</c> values of the <c>Level</c> 2 <c>TableGroup</c> rows.</summary>
    public const int Dpm20Cutoff42DistinctLevel2TemplateCodes = 563;

    /// <summary><c>BusinessTable</c> rows of <c>mTemplateOrTable</c> whose parent is a real node (<c>ParentTemplateOrTableID</c> not 0).</summary>
    public const int Dpm20Cutoff42BusinessTablesWithRealParent = 799;

    /// <summary><c>BusinessTable</c> rows of <c>mTemplateOrTable</c> that are orphans (<c>ParentTemplateOrTableID</c> = 0).</summary>
    public const int Dpm20Cutoff42OrphanBusinessTables = 48;

    /// <summary>Distinct table codes emitted (non-abstract, without DORA): distinct <c>mTable.TableCode</c>.</summary>
    public const int Dpm20Cutoff42EmittedTableCodes = 788;

    /// <summary><c>COUNT(1)</c> of <c>mTaxonomy</c> in the 4.2 reference export (<see cref="Reference42"/>).</summary>
    public const int Reference42MTaxonomyRows = 31;

    /// <summary><c>COUNT(1)</c> of <c>mReportingFramework</c> in the 4.2 reference export (<see cref="Reference42"/>).</summary>
    public const int Reference42MReportingFrameworkRows = 18;

    /// <summary>Distinct <c>mTable.TableCode</c> values in the 4.2 reference export (<see cref="Reference42"/>).</summary>
    public const int Reference42MTableCodes = 788;

    /// <summary>The DPM 1.0 release 4.1 taxonomies listed by <c>--list-taxonomies</c> (<see cref="Dpm10All41"/>).</summary>
    public const int Dpm10All41ListedTaxonomies = 128;

    // ------------------------------------------------------------------
    // DPM 1.0 (release 4.1 is the last one: no new DPM 1.0 release is published) axes, cells and
    // modules. Figures of the reference exports are measurements of those files; figures of the
    // converted selections are scoped to the selection.
    // ------------------------------------------------------------------

    /// <summary>
    /// The 3.2 reference export <c>EBA_3.2_phase_1.db</c> (information only, never an authority): a
    /// figure pinned here is a measurement of that file, not a property of the format.
    /// </summary>
    public static readonly ReleasePin Reference32 = new(
        "3.2", Schema.RepoPaths.Reference32FileName, "2026-10-23");

    /// <summary>
    /// The 4.0 reference export <c>EBA_4.0_ERRATA_5.db</c> (information only, never an authority): a
    /// figure pinned here is a measurement of that file, not a property of the format.
    /// </summary>
    public static readonly ReleasePin Reference40 = new(
        "4.0", Schema.RepoPaths.Reference40FileName, "2026-10-23");

    /// <summary>
    /// The DPM 1.0 database of release 4.1 converted for the eight taxonomies of the AxesAndCells and
    /// Modules fixtures: <c>AE 3.2</c>, <c>COREP 3.2</c>, <c>GSII 3.2</c>, <c>IF 3.2</c>, <c>COREP 4.0</c>,
    /// <c>DORA 4.0</c>, <c>IF 4.0</c> and <c>MICA 4.0</c>.
    /// </summary>
    public static readonly ReleasePin Dpm10Selected8Of41 = new(
        "4.1", Schema.RepoPaths.AccessDatabaseFileName + " (eight taxonomies of the AxesAndCells/Modules fixtures)", "2026-10-23");

    /// <summary><c>COUNT(1)</c> of <c>mAxis</c> in the 3.2 reference export.</summary>
    public const int Reference32MAxisRows = 522;

    /// <summary><c>COUNT(1)</c> of <c>mAxisOrdinate</c> in the 3.2 reference export.</summary>
    public const int Reference32MAxisOrdinateRows = 7615;

    /// <summary><c>COUNT(1)</c> of <c>mTableCell</c> in the 3.2 reference export.</summary>
    public const int Reference32MTableCellRows = 129549;

    /// <summary><c>COUNT(1)</c> of <c>mCellPosition</c> in the 3.2 reference export.</summary>
    public const int Reference32MCellPositionRows = 358946;

    /// <summary><c>COUNT(1)</c> of <c>mOpenAxisValueRestriction</c> in the 3.2 reference export.</summary>
    public const int Reference32MOpenAxisValueRestrictionRows = 58;

    /// <summary>Distinct (taxonomy, table code) keys in the 3.2 reference export.</summary>
    public const int Reference32Tables = 217;

    /// <summary>Closed axes, keyed by (taxonomy, table code, orientation), in the 3.2 reference export.</summary>
    public const int Reference32ClosedAxisLabels = 433;

    /// <summary>Closed ordinates of the 3.2 reference export matched by path in the generated output.</summary>
    public const int Reference32ClosedOrdinatesCompared = 7526;

    /// <summary>Cells of the 3.2 reference export with <c>IsShaded</c> = 0 (equivalently, with a <c>DPS</c>).</summary>
    public const int Reference32UnshadedCells = 69357;

    /// <summary>Cells of the 3.2 reference export whose <c>DPS</c> has a three-part bracket (starting member).</summary>
    public const int Reference32ThreePartBracketCells = 12031;

    /// <summary>Leaf ordinates (those that carry cells) of the 3.2 reference export compared by path.</summary>
    public const int Reference32LeafOrdinatesWithCells = 6877;

    /// <summary>Modules (distinct taxonomy and module code) in the 3.2 reference export.</summary>
    public const int Reference32Modules = 13;

    /// <summary><c>COUNT(1)</c> of <c>mModuleBusinessTemplate</c> in the 3.2 reference export.</summary>
    public const int Reference32ModuleBusinessTemplateRows = 35;

    /// <summary>Comparable tables of the 4.0 reference export (<c>corep 3.4</c> is not in the Access source).</summary>
    public const int Reference40ComparableTables = 150;

    /// <summary>Cells of the 4.0 reference export matched by position in the generated output.</summary>
    public const int Reference40CellsCompared = 59025;

    /// <summary>Modules in the 4.0 reference export.</summary>
    public const int Reference40Modules = 5;

    /// <summary>Cells compared by the <c>BusinessCode</c> reconstruction over <c>--all</c> of the 4.1 database.</summary>
    public const int Dpm10All41CellsCompared = 904204;

    /// <summary><c>SkippedDefaultMemberResets</c> of <c>AxisAndCellLoader.Result</c> (no table holds it).</summary>
    public const int Dpm10Selected8SkippedDefaultMemberResets = 0;

    /// <summary><c>MetricInheritedFromAncestorCellCount</c> of <c>AxisAndCellLoader.Result</c> (no table holds it).</summary>
    public const int Dpm10Selected8MetricInheritedFromAncestorCells = 0;

    /// <summary>Asserts a pinned figure; the failure message prints the scope of the pin.</summary>
    public static void AssertPinned(ReleasePin pin, string what, long expected, long actual)
        => Xunit.Assert.True(expected == actual, Message(pin, what, expected, actual));

    /// <summary>Failure message: what was compared plus the scope of the pin.</summary>
    public static string Message(ReleasePin pin, string what, object expected, object actual)
        => $"{what}: expected {expected}, actual {actual} ({pin}).";
}
