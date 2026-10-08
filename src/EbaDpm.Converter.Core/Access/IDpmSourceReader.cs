namespace EbaDpm.Converter.Core.Access;

/// <summary>
/// Seam for access to the DPM data source. Currently the only implementation is
/// <see cref="Dpm10AccessReader"/>, over the DPM 1.0 Access database; the DPM 2.0 reader has its
/// own, separate pipeline.
///
/// Exposes typed reads of the dictionary, template, module and cell entities. All reads are
/// streaming: none loads the whole table into memory before returning it, unless the source
/// itself is already small (e.g. Owner, ReportingFramework).
/// </summary>
public interface IDpmSourceReader : IDisposable
{
    /// <summary>
    /// Opens the connection to the source. Must be called before any read.
    /// </summary>
    void Open();

    /// <summary>
    /// Closes the connection to the source, if it was open.
    /// </summary>
    void Close();

    /// <summary>Reads the whole <c>Owner</c> table (a single row in the v4.1 source).</summary>
    IEnumerable<AccessOwnerRow> ReadOwners();

    /// <summary>Reads the whole <c>ReportingFramework</c> table (direct load, unfiltered).</summary>
    IEnumerable<AccessReportingFrameworkRow> ReadReportingFrameworks();

    /// <summary>Reads the whole <c>Taxonomy</c> table, unfiltered.</summary>
    IEnumerable<AccessTaxonomyRow> ReadTaxonomies();

    /// <summary>
    /// Reads from <c>Concept</c> only the rows whose <c>ConceptID</c> is in
    /// <paramref name="conceptIds"/>. Intended for small censuses (the concepts reachable from
    /// the entities already emitted), not for walking the whole table (over 120,000 rows).
    /// </summary>
    IEnumerable<AccessConceptRow> ReadConceptsByIds(IEnumerable<int> conceptIds);

    /// <summary>
    /// Maximum <c>ConceptID</c> of the whole <c>Concept</c> table, aggregated by the Access
    /// engine itself: the table is not transferred. Only used as a fallback when synthesizing
    /// IDs, when the preferred synthetic ID is already taken.
    /// </summary>
    int ReadMaxConceptId();

    /// <summary>
    /// Counts the <c>TaxonomyTableVersion</c> rows per <c>TaxonomyID</c>. Used by
    /// <c>--list-taxonomies</c> to show the number of tables of each taxonomy.
    /// </summary>
    IEnumerable<AccessTaxonomyTableCount> ReadTaxonomyTableCounts();

    // ------------------------------------------------------------------
    // Dictionary
    // ------------------------------------------------------------------

    /// <summary>Reads the whole <c>Domain</c> table, unfiltered by taxonomy.</summary>
    IEnumerable<AccessDomainRow> ReadDomains();

    /// <summary>Reads the whole <c>Member</c> table, unfiltered by taxonomy.</summary>
    IEnumerable<AccessMemberRow> ReadMembers();

    /// <summary>Reads the whole <c>Dimension</c> table, unfiltered by taxonomy.</summary>
    IEnumerable<AccessDimensionRow> ReadDimensions();

    /// <summary>Reads the whole <c>Metric</c> table, unfiltered by taxonomy.</summary>
    IEnumerable<AccessMetricRow> ReadMetrics();

    /// <summary>Reads the whole <c>Hierarchy</c> table, unfiltered by taxonomy.</summary>
    IEnumerable<AccessHierarchyRow> ReadHierarchies();

    /// <summary>
    /// Reads the whole <c>HierarchyNode</c> table, ordered by (<c>HierarchyID</c>,
    /// <c>MemberID</c>) so that a deterministic synthetic <c>HierarchyNodeID</c> can be assigned
    /// while streaming, without materializing the table.
    /// </summary>
    IEnumerable<AccessHierarchyNodeRow> ReadHierarchyNodes();

    /// <summary>Reads the whole <c>DataType</c> table (9 rows): denormalization lookup.</summary>
    IEnumerable<AccessDataTypeRow> ReadDataTypes();

    /// <summary>Reads the whole <c>FlowType</c> table (2 rows): denormalization lookup.</summary>
    IEnumerable<AccessFlowTypeRow> ReadFlowTypes();

    // ------------------------------------------------------------------
    // Templates and tables
    // ------------------------------------------------------------------

    /// <summary>Reads the whole <c>TableGroup</c> table, unfiltered by taxonomy.</summary>
    IEnumerable<AccessTableGroupRow> ReadTableGroups();

    /// <summary>Reads the whole <c>Template</c> table, unfiltered by taxonomy.</summary>
    IEnumerable<AccessTemplateRow> ReadTemplates();

    /// <summary>Reads the whole <c>TaxonomyTableVersion</c> table, unfiltered by taxonomy.</summary>
    IEnumerable<AccessTaxonomyTableVersionRow> ReadTaxonomyTableVersions();

    /// <summary>
    /// Reads from <c>TableVersion</c> only the rows whose <c>TableVID</c> is in
    /// <paramref name="tableVIds"/>. Intended for the census of tables reachable from the
    /// taxonomy selection, not for walking the whole table.
    /// </summary>
    IEnumerable<AccessTableVersionRow> ReadTableVersionsByIds(IEnumerable<int> tableVIds);

    /// <summary>
    /// Reads from <c>Table</c> only the rows whose <c>TableID</c> is in
    /// <paramref name="tableIds"/>.
    /// </summary>
    IEnumerable<AccessTableRow> ReadTablesByIds(IEnumerable<int> tableIds);

    // ------------------------------------------------------------------
    // Modules
    // ------------------------------------------------------------------

    /// <summary>
    /// Reads the whole <c>Module</c> table, unfiltered by taxonomy.
    /// Does not read <c>ConceptualModuleID</c>, <c>Version</c>, <c>FromDate</c>, <c>ToDate</c> or
    /// <c>isDocumentModule</c>: none of them has a target column.
    /// </summary>
    IEnumerable<AccessModuleRow> ReadModules();

    /// <summary>
    /// Reads the whole <c>ModuleTableVersion</c> table, unfiltered. Derivation path of
    /// <c>mModuleBusinessTemplate</c>, to be combined in memory with
    /// <see cref="ReadTaxonomyTableVersions"/> by <c>(TaxonomyID, TableVID)</c>.
    /// </summary>
    IEnumerable<AccessModuleTableVersionRow> ReadModuleTableVersions();

    // ------------------------------------------------------------------
    // Cells and open-axis restrictions
    // ------------------------------------------------------------------

    /// <summary>
    /// Reads from <c>Axis</c> only the axes whose <c>TableVID</c> is in
    /// <paramref name="tableVIds"/>.
    /// </summary>
    IEnumerable<AccessAxisRow> ReadAxesByTableVIds(IEnumerable<int> tableVIds);

    /// <summary>
    /// Reads <c>AxisOrdinate</c> (all columns), filtered by <c>Axis.TableVID</c> in
    /// <paramref name="tableVIds"/> via <c>INNER JOIN Axis</c>.
    /// </summary>
    IEnumerable<AccessAxisOrdinateRow> ReadAxisOrdinatesByTableVIds(IEnumerable<int> tableVIds);

    /// <summary>
    /// Reads <c>OrdinateCategorisation</c>, filtered by <c>Axis.TableVID</c> in
    /// <paramref name="tableVIds"/> via <c>INNER JOIN AxisOrdinate INNER JOIN Axis</c>. Note that
    /// the open-value sentinel is <c>MemberID = 999</c>.
    /// </summary>
    IEnumerable<AccessOrdinateCategorisationRow> ReadOrdinateCategorisationsByTableVIds(IEnumerable<int> tableVIds);

    /// <summary>
    /// Reads from <c>TableCell</c> only the rows whose <c>TableVID</c> is in
    /// <paramref name="tableVIds"/>. Does not read <c>DataPointVID</c> (the <c>DataPoint</c>
    /// entity is discarded from the target).
    /// </summary>
    IEnumerable<AccessTableCellRow> ReadTableCellsByTableVIds(IEnumerable<int> tableVIds);

    /// <summary>
    /// Reads <c>CellPosition</c>, filtered by <c>TableCell.TableVID</c> in
    /// <paramref name="tableVIds"/> via <c>INNER JOIN TableCell</c> — the hot spot of the project:
    /// about 2.4 million rows in the v4.1 source, the largest table in the Access database.
    /// Strictly streaming (<c>yield return</c> over the underlying
    /// <see cref="System.Data.OleDb.OleDbDataReader"/>), without materializing any intermediate
    /// list. Ordered by <c>CellID</c> **within each chunk** into which the <c>IN</c> clause of
    /// <paramref name="tableVIds"/> is split: if the selection fits in a single chunk, the whole
    /// result is ordered by <c>CellID</c>.
    /// </summary>
    IEnumerable<AccessCellPositionRow> ReadCellPositionsByTableVIds(IEnumerable<int> tableVIds);

    /// <summary>
    /// Reads the whole <c>OpenMemberRestriction</c> table, unfiltered. It is the only source of
    /// content for <c>mOpenAxisValueRestriction</c>.
    /// </summary>
    IEnumerable<AccessOpenMemberRestrictionRow> ReadOpenMemberRestrictions();

    /// <summary>
    /// Reads the whole <c>OpenAxisValueRestriction</c> table, unfiltered. It is **NOT** the
    /// source of <c>mOpenAxisValueRestriction</c> — that comes from
    /// <see cref="ReadOpenMemberRestrictions"/> plus <see cref="ReadOrdinateCategorisationsByTableVIds"/>.
    /// It is read only for a coherence assertion by the tests.
    /// </summary>
    IEnumerable<AccessOpenAxisValueRestrictionRow> ReadOpenAxisValueRestrictions();
}
