namespace EbaDpm.Converter.Core.Access;

/// <summary>
/// Row of the Access <c>Owner</c> table (a single row in the v4.1 source). The target
/// <c>mOwner</c> adds two constant rows that do not come from this row.
/// </summary>
public sealed record AccessOwnerRow(
    int OwnerId,
    string? OwnerName,
    string? OwnerNamespace,
    string? OwnerLocation,
    string? OwnerPrefix,
    string? OwnerCopyright,
    int? ParentOwnerId,
    int? ConceptId);

/// <summary>Row of <c>ReportingFramework</c>. Direct load.</summary>
public sealed record AccessReportingFrameworkRow(
    int FrameworkId,
    string? FrameworkCode,
    string? FrameworkLabel,
    int? ConceptId);

/// <summary>
/// Row of <c>Taxonomy</c>. It is the unit on which the mandatory taxonomy filter acts.
/// </summary>
public sealed record AccessTaxonomyRow(
    int TaxonomyId,
    int? FrameworkId,
    string TaxonomyCode,
    string? TaxonomyLabel,
    string? TechnicalStandard,
    DateTime? NotionalPublicationDate,
    DateTime? ActualPublicationDate,
    string DpmPackageCode,
    int? ConceptId);

/// <summary>
/// Row of <c>Concept</c>, the cross-cutting entity (over 120,000 rows in the v4.1 source). The
/// Access database has no <c>ReleaseID</c>: it is derived.
/// </summary>
public sealed record AccessConceptRow(
    int ConceptId,
    string? ConceptType,
    int? OwnerId,
    DateTime? CreationDate,
    DateTime? ModificationDate,
    DateTime? FromDate,
    DateTime? ToDate);

/// <summary>
/// Number of table versions (<c>TaxonomyTableVersion</c>) associated with a taxonomy. Used by
/// <c>--list-taxonomies</c>.
/// </summary>
public sealed record AccessTaxonomyTableCount(int TaxonomyId, int TableCount);

// ------------------------------------------------------------------------
// Dictionary: Domain, Member, Dimension, Metric, Hierarchy, HierarchyNode
// and their denormalization lookup tables.
// ------------------------------------------------------------------------

/// <summary>Row of <c>Domain</c>.</summary>
public sealed record AccessDomainRow(
    int DomainId,
    string DomainCode,
    string DomainLabel,
    bool IsTypedDomain,
    bool IsExternalRefData,
    string? ReferenceDataSource,
    int? DataTypeId,
    string? DomainDescription,
    string DomainXbrlCode,
    int? ConceptId);

/// <summary>Row of <c>Member</c>. Includes the ones that are metrics.</summary>
public sealed record AccessMemberRow(
    int MemberId,
    int? DomainId,
    string MemberCode,
    string MemberLabel,
    bool IsDefaultMember,
    string? MemberXbrlCode,
    string? MemberDescription,
    int? ConceptId);

/// <summary>Row of <c>Dimension</c>.</summary>
public sealed record AccessDimensionRow(
    int DimensionId,
    int DomainId,
    string DimensionCode,
    string DimensionLabel,
    string? DimensionDescription,
    bool IsImpliedIfNotExplicitlyModelled,
    string? DimensionXbrlCode,
    int? ConceptId);

/// <summary>
/// Row of <c>Metric</c>. <c>MetricID</c> is, by an FK of the Access database itself, a
/// <c>Member.MemberID</c>: a metric is a subtype of member.
/// </summary>
public sealed record AccessMetricRow(
    int MetricId,
    int DataTypeId,
    int FlowTypeId,
    int? CodeDomainId,
    int? CodeSubdomainId,
    int? StringListId,
    string? RequiredDataSign,
    string? TypicalDataSign,
    string? Additivity);

/// <summary>Row of <c>Hierarchy</c>.</summary>
public sealed record AccessHierarchyRow(
    int HierarchyId,
    string HierarchyCode,
    string? HierarchyLabel,
    int? DomainId,
    string? HierarchyDescription,
    int? ConceptId);

/// <summary>
/// Row of <c>HierarchyNode</c>. It has no ID of its own in the Access database (the key is the
/// composite <c>HierarchyID</c>+<c>MemberID</c>); the target does require a synthetic
/// <c>HierarchyNodeID</c> (see <c>DictionaryLoader</c>).
/// </summary>
public sealed record AccessHierarchyNodeRow(
    int HierarchyId,
    int MemberId,
    bool IsAbstract,
    string? ComparisonOperator,
    string? UnaryOperator,
    int Order,
    int Level,
    string? Path,
    int? ParentMemberId,
    int? ConceptId);

/// <summary>Row of <c>DataType</c>: denormalization lookup table.</summary>
public sealed record AccessDataTypeRow(int DataTypeId, string? DataTypeCode, string DataTypeLabel);

/// <summary>Row of <c>FlowType</c>: denormalization lookup table.</summary>
public sealed record AccessFlowTypeRow(int FlowTypeId, string? FlowTypeCode, string FlowTypeLabel);

// ------------------------------------------------------------------------
// Templates and tables: TableGroup, Template, TaxonomyTableVersion, TableVersion, Table.
// ------------------------------------------------------------------------

/// <summary>
/// Row of <c>TableGroup</c>. Instantiated per taxonomy (<c>TaxonomyID</c>), unlike
/// <c>TemplateGroup</c> (per framework, with no target). It is the source of level 1 of
/// <c>mTemplateOrTable</c>.
/// </summary>
public sealed record AccessTableGroupRow(
    int TableGroupId,
    int? TaxonomyId,
    string TableGroupCode,
    string? TableGroupLabel,
    int? Order,
    int? ConceptId);

/// <summary>
/// Row of <c>Template</c>. Source of level 2 of <c>mTemplateOrTable</c>, once combined with
/// <c>TaxonomyTableVersion</c> to know in which taxonomy(ies) and under which <c>TableGroup</c>
/// it is used.
/// </summary>
public sealed record AccessTemplateRow(
    int TemplateId,
    string TemplateCode,
    string TemplateLabel,
    int? ConceptId);

/// <summary>
/// Row of <c>TaxonomyTableVersion</c>, the taxonomy-to-table relation of the Access database.
/// It is both the L2-to-L1 link (equivalent to <c>TableGroupTemplates</c>, preferred because the
/// taxonomy filter is naturally applied here already) and the source of <c>mTaxonomyTable</c>.
/// </summary>
public sealed record AccessTaxonomyTableVersionRow(
    int TaxonomyId,
    int TableVId,
    int? TemplateId,
    int? TableGroupId,
    bool IsSimpleReuse);

/// <summary>
/// Row of <c>TableVersion</c>. Source of <c>mTable</c> and of the <c>BusinessTable</c> level of
/// <c>mTemplateOrTable</c>. Note: <c>XbrlTableCode</c> —not <c>TableVersionCode</c>— is the one
/// that matches <c>mTable.TableCode</c>.
/// </summary>
public sealed record AccessTableVersionRow(
    int TableVId,
    int? TableId,
    string? TableVersionCode,
    string? TableVersionLabel,
    string? XbrlFilingIndicatorCode,
    string? XbrlTableCode,
    DateTime? FromDate,
    DateTime? ToDate,
    int? ConceptId);

/// <summary>
/// Row of <c>Table</c>, the "table" entity common to its versions (<c>TableVersion</c>, 1:N).
///
/// Note that this entity produces NO target row (<c>mTable</c> comes from <c>TableVersion</c>),
/// so its <c>ConceptID</c> describes nothing: using it would leave dangling concepts in
/// <c>mConcept</c>. <c>EbaDpm.Converter.Core.Mapping.TemplateOrTableLoader</c> therefore no longer
/// reads this table from the Access database at all; <c>mTemplateOrTable(BusinessTable).ConceptID</c>
/// is <c>TableVersion.ConceptID</c>, shared with <c>mTable.ConceptID</c>.
/// </summary>
public sealed record AccessTableRow(
    int TableId,
    int? TemplateId,
    string OriginalTableCode,
    string OriginalTableLabel,
    int? ConceptId);

// ------------------------------------------------------------------------
// Modules: Module and ModuleTableVersion. Access.ConceptualModule is NOT read:
// mConceptualModule is a synthetic copy of mModule, not a source table of its own.
// ------------------------------------------------------------------------

/// <summary>
/// Row of <c>Module</c>. Source of <c>mModule</c>. The Access <c>ConceptualModuleID</c> is
/// **not read**: <c>mConceptualModule</c> is a synthetic 1:1 copy of <c>mModule</c>, not a
/// translation of <c>Access.ConceptualModule</c>.
/// </summary>
public sealed record AccessModuleRow(
    int ModuleId,
    int TaxonomyId,
    string ModuleCode,
    string ModuleLabel,
    string? XbrlSchemaRef,
    int? ConceptId);

/// <summary>
/// Row of <c>ModuleTableVersion</c>: module-to-table relation, the path used to derive the set
/// of groups of <c>mModuleBusinessTemplate</c>. It is preferred over <c>ModuleTableOrGroup</c>
/// because the latter is incomplete in the v4.1 Access database (only 20 of 415 modules have
/// tables and no rows). It is combined in memory with <c>TaxonomyTableVersion</c>
/// (<see cref="AccessTaxonomyTableVersionRow"/>) by <c>(TaxonomyID, TableVID)</c>: Access does
/// not support a composite <c>ON</c> in a <c>LEFT JOIN</c>.
/// </summary>
public sealed record AccessModuleTableVersionRow(int ModuleId, int TableVId);

// ------------------------------------------------------------------------
// Cells and open-axis restrictions: Axis, AxisOrdinate, OrdinateCategorisation,
// TableCell, CellPosition, OpenMemberRestriction and OpenAxisValueRestriction.
// ------------------------------------------------------------------------

/// <summary>
/// Row of <c>Axis</c>: an axis (X/Y/Z) of a table version.
/// </summary>
public sealed record AccessAxisRow(
    int AxisId,
    int? TableVId,
    string? AxisOrientation,
    string? AxisLabel,
    int? AxisOrder,
    bool IsOpenAxis,
    int? ConceptId);

/// <summary>
/// Row of <c>AxisOrdinate</c>: a concrete position of an axis.
/// Note that <c>OrdinateCode</c> is <c>WChar(4)</c> and comes **space-padded** in the source
/// (about 40% of the rows carry padding); this record already returns <see cref="OrdinateCode"/>
/// with <see cref="string.Trim"/> applied. The open-axis sentinel is <c>'999 '</c> untrimmed,
/// <c>'999'</c> trimmed — not to be confused with the <c>MemberID</c> sentinel.
/// </summary>
public sealed record AccessAxisOrdinateRow(
    int OrdinateId,
    int AxisId,
    bool IsAbstractHeader,
    string OrdinateCode,
    string OrdinateLabel,
    int Order,
    int Level,
    string? Path,
    int? ParentOrdinateId,
    bool DisplayBeforeChildren,
    string? CategorisationKey,
    bool IsRowKey,
    string? RequiredDataSign,
    string? TypicalDataSign,
    int? ConceptId);

/// <summary>
/// Row of <c>OrdinateCategorisation</c>: the dimension-member pair associated with an axis
/// position. Note: the "open value" sentinel is <c>MemberID = 999</c>, NOT <c>9999</c> — that
/// value is a real NACE member in the Access database. The sentinel of the target is indeed
/// <c>9999</c>: they are different databases with different sentinels.
/// </summary>
public sealed record AccessOrdinateCategorisationRow(
    int OrdinateId,
    int DimensionId,
    int MemberId,
    int? RestrictionId);

/// <summary>
/// Row of <c>TableCell</c>. <c>DataPointVID</c> is NOT read: the <c>DataPoint</c> entity is
/// discarded from the target, so that link has no consumer.
/// </summary>
public sealed record AccessTableCellRow(
    int CellId,
    int? TableVId,
    bool IsShaded,
    string CellCode);

/// <summary>
/// Row of <c>CellPosition</c> (about 2.4 million rows in the v4.1 source: the largest table of
/// the Access database), the cell-to-ordinate intersection table. <c>mCellPosition</c> has no
/// rule of its own: it is the mechanical consequence of this table plus the axis transformation.
/// </summary>
public sealed record AccessCellPositionRow(int CellId, int OrdinateId);

/// <summary>
/// Row of <c>OpenMemberRestriction</c>: the real content of an open-axis restriction. It is the
/// only source of content for <c>mOpenAxisValueRestriction</c>; the axis-to-restriction link
/// comes from <c>OrdinateCategorisation.RestrictionID</c> of the sentinel ordinate, NOT from
/// <c>OpenAxisValueRestriction</c>.
/// </summary>
public sealed record AccessOpenMemberRestrictionRow(
    int RestrictionId,
    int? HierarchyId,
    int? MemberId,
    bool MemberIncluded,
    bool AllowsDefaultMember,
    bool IgnoreMemberId);

/// <summary>
/// Row of <c>OpenAxisValueRestriction</c>. Despite the name, this table does NOT feed
/// <c>mOpenAxisValueRestriction</c>. It is a per-axis roll-up that is never wrong but is
/// ambiguous (it does not attribute a dimension: several restrictions have more than one
/// candidate) and carries expired restrictions. It is read **only** for the cross-coherence
/// assertion: the emitted hierarchy must always belong to the set this table predicts for the
/// same axis.
/// </summary>
public sealed record AccessOpenAxisValueRestrictionRow(int AxisId, int RestrictionId);
