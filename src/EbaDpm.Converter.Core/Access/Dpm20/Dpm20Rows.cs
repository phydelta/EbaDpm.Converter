namespace EbaDpm.Converter.Core.Access.Dpm20;

// ------------------------------------------------------------------------
// Typed rows specific to the DPM 2.0 source. They are NOT the rows of AccessRows.cs (those are
// the DPM 1.0 shape): DPM 2.0 is a structurally different model and this pipeline is new and
// parallel. The only deliberate exception is AccessTaxonomyRow, reused on purpose by
// Dpm20TaxonomyDeriver because the target taxonomy has the same shape in both pipelines even
// though its origin differs: in DPM 1.0 it is read, in DPM 2.0 it is derived.
// ------------------------------------------------------------------------

/// <summary>
/// Row of <c>[Release]</c> (5 rows: 3.4, 3.5, 4.0, 4.1, 4.2). Not versioned.
///
/// <see cref="Date"/> is TEXT already in ISO format (<c>"2024-02-06"</c>...) — it is copied as is
/// to <c>mRelease.PublicationDate</c>, never passed through <see cref="DateTime"/> (culture and
/// time-zone non-determinism). <see cref="Status"/> and <see cref="Description"/> may be NULL
/// (<see cref="Description"/> only exists in 4.1 and 4.2). <see cref="IsCurrent"/> is copied to
/// <c>mRelease.IsCurrent</c> with a DIRECT MAPPING, without normalizing: it is a DYNAMIC source
/// value (true only for the most recent release of this Access database) that changes from one
/// Access publication to the next, so <c>Dpm20SkeletonLoader</c> does not pin it to any constant
/// nor "fix" it to match any reference.
/// </summary>
public sealed record Dpm20ReleaseRow(
    int ReleaseId,
    string Code,
    string? Date,
    string? Status,
    string? Description,
    bool IsCurrent);

/// <summary>Row of <c>Framework</c>. Not versioned.</summary>
public sealed record Dpm20FrameworkRow(int FrameworkId, string Code, string Name);

/// <summary>Row of <c>Organisation</c>. Not versioned.</summary>
public sealed record Dpm20OrganisationRow(int OrgId, string Name, string? Acronym, int? IdPrefix);

/// <summary>
/// Row of <c>[Module]</c>. Not versioned: versioning lives in <see cref="Dpm20ModuleVersionRow"/>.
/// A module belongs to a single framework.
///
/// <see cref="IsDocumentModule"/>: true for exactly two of the current modules
/// (<c>P3_NONREM_DIS_DOCS</c>, <c>P3_REM_DIS_DOCS</c> — links to PDFs, with no table at all).
/// They are excluded from <c>mModule</c>, together with the DORA modules.
/// </summary>
public sealed record Dpm20ModuleRow(int ModuleId, int FrameworkId, bool IsDocumentModule);

/// <summary>
/// Row of <c>ModuleVersion</c>. Versioned table: only
/// <see cref="Dpm20AccessReader.ReadModuleVersions"/> exposes it, already filtered by the
/// current-version predicate for the cutoff release R: <c>StartReleaseID &lt;= R AND (EndReleaseID
/// IS NULL OR EndReleaseID &gt; R)</c>.
/// </summary>
public sealed record Dpm20ModuleVersionRow(
    int ModuleVId,
    int ModuleId,
    string Code,
    string Name,
    int StartReleaseId,
    int? EndReleaseId);

/// <summary>
/// Row of <c>ModuleVersionComposition</c>. It is NOT itself versioned: whether a (module, table)
/// pair is current is decided by the currency of <see cref="ModuleVId"/> and of
/// <see cref="TableVId"/> separately, each in its own table.
/// </summary>
public sealed record Dpm20ModuleVersionCompositionRow(int ModuleVId, int TableVId);

/// <summary>
/// Row of <c>TableVersion</c> joined with <c>[Table]</c>. Versioned table: only
/// <see cref="Dpm20AccessReader.ReadTableVersions"/> exposes it, already filtered by the
/// current-version predicate. It includes the ABSTRACT table versions: filtering by
/// <see cref="IsAbstract"/> is the mapping's responsibility, not the reader's — <c>mTable</c>
/// excludes them but <c>mTemplateOrTable</c> needs them as its <c>eba_tg...</c> template nodes.
///
/// <see cref="ContextId"/>: the context of the TABLE VERSION ITSELF — the fifth source of
/// <c>mOrdinateCategorisation</c>, distinct from the context of each header
/// (<see cref="Dpm20HeaderVersionRow.ContextId"/>). Only a few current versions have it.
/// </summary>
public sealed record Dpm20TableVersionRow(
    int TableVId,
    string Code,
    string Name,
    int TableId,
    int? AbstractTableId,
    int StartReleaseId,
    int? EndReleaseId,
    bool IsAbstract,
    int? ContextId);

// ------------------------------------------------------------------------
// mDomain, mDomainUnion, mMember, mHierarchy. None of these five source tables is filtered by
// the current-version predicate — the dictionary is shared across taxonomies.
// ------------------------------------------------------------------------

/// <summary>Row of <c>Category</c>. Not versioned. Source of <c>mDomain</c>.</summary>
public sealed record Dpm20CategoryRow(int CategoryId, string Code, string? Name, string? Description, bool IsEnumerated);

/// <summary>
/// Row of <c>ItemCategory</c>: item-to-domain membership. It versions the CODE, not just the
/// membership: a renamed <c>ItemID</c> leaves two rows with different <see cref="Code"/> values,
/// and the target emits both. Source of <c>mMember</c>.
///
/// <see cref="StartReleaseId"/>/<see cref="EndReleaseId"/> are also read here, unfiltered (the
/// dictionary is not pruned by release): in <c>_PR</c> they are the validity window of a
/// property's CODE, and a few property-dimensions have more than one code with different windows
/// (e.g. <c>LEA</c> in <c>[2,3)</c> and <c>qLEA</c> in <c>[3,-)</c>, the SAME property).
/// <see cref="Dpm20DictionaryLoader"/> crosses them with the <c>PropertyCategory</c> windows to
/// decide which code each <c>mDimension</c> row carries.
/// </summary>
public sealed record Dpm20ItemCategoryRow(int ItemId, int CategoryId, string Code, bool IsDefaultItem, int StartReleaseId, int? EndReleaseId);

/// <summary>Row of <c>Item</c>: the name for <c>mMember.MemberLabel</c>.</summary>
public sealed record Dpm20ItemRow(int ItemId, string? Name);

/// <summary>
/// Row of <c>SuperCategoryComposition</c>: <c>mDomainUnion</c>, DIRECT translation —
/// <c>SuperCategoryID</c> to <c>UnionDomainID</c>, <c>CategoryID</c> to <c>UnitedDomainID</c>.
/// The union is NOT materialized: this row only declares the pair, nobody expands members.
/// </summary>
public sealed record Dpm20SuperCategoryCompositionRow(int SuperCategoryId, int CategoryId);

/// <summary>
/// Row of <c>SubCategory</c>: <c>mHierarchy</c>, 1 to 1. It carries no <c>Description</c> of its
/// own in the source (constant <c>''</c> in the target).
/// </summary>
public sealed record Dpm20SubCategoryRow(int SubCategoryId, int CategoryId, string Code, string? Name);

// ------------------------------------------------------------------------
// mMetric, mDimension. As above, none of these reads is filtered by the current-version
// predicate: the dictionary is shared across taxonomies.
// ------------------------------------------------------------------------

/// <summary>
/// Row of <c>[DataType]</c> (codes <c>i, r, s, b, t, d, e, m, p, es</c>, plus those the target
/// does not translate — <c>dt</c>, <c>u</c>, <c>o</c>). Not versioned. Source of
/// <c>mMetric.DataType</c> (translated) and of the <c>_NA</c> split into <c>INT_NA</c>/
/// <c>STR_NA</c> of <c>mDimension.DomainID</c> (by the property's OWN data type, untranslated).
/// </summary>
public sealed record Dpm20DataTypeRow(int DataTypeId, string Code);

/// <summary>
/// Row of <c>Property</c>: its own <see cref="DataTypeId"/>. Property.PropertyID = Item.ItemID:
/// this row is combined by Id with <c>ItemCategory</c>/<c>Item</c>, there is no direct JOIN in
/// the source. <see cref="DataTypeId"/> may be NULL.
/// </summary>
public sealed record Dpm20PropertyRow(int PropertyId, int? DataTypeId);

/// <summary>
/// Row of <c>PropertyCategory</c>: the domain OF THE VALUES of a property — NOT its membership
/// in <c>_PR</c>, which lives in <c>ItemCategory</c>. Source of <c>mDimension.DomainID</c> and of
/// the release suffix of <c>DimensionXBRLCode</c> (<see cref="StartReleaseId"/> of THIS row). A
/// property may have more than one row: that is how the split of <c>mDimension</c> arises without
/// coding it as a special case. <see cref="EndReleaseId"/> is also read to cross this row's window
/// with that of the <c>ItemCategory</c> code (see <see cref="Dpm20ItemCategoryRow"/>).
/// </summary>
public sealed record Dpm20PropertyCategoryRow(int PropertyId, int CategoryId, int StartReleaseId, int? EndReleaseId);

/// <summary>
/// <c>PropertyID</c> to <c>SubCategoryID</c> via <c>HeaderVersion.SubCategoryVID</c> to
/// <c>SubCategoryVersion.SubCategoryID</c>: the only source table that links a property with a
/// subcategory. Source of <c>mMetric.ReferencedHierarchyID</c>. <see cref="HeaderVId"/> is read
/// only to be able to order deterministically when a property has more than one candidate (see
/// <see cref="Dpm20DictionaryLoader"/>).
/// </summary>
public sealed record Dpm20HeaderVersionSubCategoryRow(int PropertyId, int SubCategoryId, int HeaderVId);

// ------------------------------------------------------------------------
// Structure: mTable, mTaxonomyTable, mTemplateOrTable.
// ------------------------------------------------------------------------

/// <summary>
/// Row of <c>TableGroup</c> filtered by the current-version predicate: the source's template
/// group, root of the <c>mTemplateOrTable</c> tree. <see cref="Type"/> is <c>'templateGroup'</c>
/// in every row (a single stratum of groups): it is read anyway so as not to assume it without
/// checking it in the source itself. <see cref="Name"/> is the source of
/// <c>mTemplateOrTable.TemplateOrTableLabel</c> of the L1 node.
/// </summary>
public sealed record Dpm20TableGroupRow(int TableGroupId, string Code, string? Name, string? Type, int StartReleaseId, int? EndReleaseId);

/// <summary>
/// Row of <c>TableGroupComposition</c> filtered by the current-version predicate: the (group,
/// CONCRETE table) pair. <see cref="TableId"/> is <c>Table.TableID</c> (the entity), NEVER a
/// <c>TableVID</c> — and <c>TableGroupComposition</c> never points to an abstract table: the
/// abstract template is resolved through its concrete children. <c>Order</c> is deliberately NOT
/// read: the source <c>Order</c> matches the target in very few cases — the target renumbers.
/// </summary>
public sealed record Dpm20TableGroupCompositionRow(int TableGroupId, int TableId);

// ------------------------------------------------------------------------
// mAxis, mTableAxis, mAxisOrdinate, mTableCell, mCellPosition, mOrdinateCategorisation.
// `Header` merges Axis+AxisOrdinate: there is no separate `Axis` table in the source, the axis
// is DERIVED by grouping by (TableVID, Direction) or by key header. None of these rows is
// filtered by the current-version predicate: they are read bounded to the set of
// TableVID/TableID/HeaderVID/ContextID that are needed (those of the selected tables,
// including the abstract tables reachable by closure).
// ------------------------------------------------------------------------

/// <summary>
/// Row of <c>TableVersionHeader</c>: the position of a header IN A CONCRETE TABLE VERSION — the
/// tree (<see cref="ParentHeaderId"/>, <see cref="Order"/>) and whether that position is abstract
/// (<see cref="IsAbstract"/>; pruning happens AFTER the transposition step).
/// <see cref="HeaderVId"/> is already the <c>HeaderVersion</c> version resolved for this
/// <c>TableVID</c> — there is no need to re-filter by release.
/// </summary>
public sealed record Dpm20TableVersionHeaderRow(
    int TableVId, int HeaderId, int HeaderVId, int? ParentHeaderId, int Order, bool IsAbstract);

/// <summary>
/// Row of <c>Header</c>: the identity of the header, tied to <c>Table</c> (the ENTITY,
/// <see cref="TableId"/> — never a <c>TableVID</c>, same as <c>Cell.TableID</c>).
/// <see cref="Direction"/> is one of {X, Y, Z} and <see cref="IsKey"/> is the discriminator: a
/// key header is, by itself, an open axis (transposed); a NON-key one survives (if it survives
/// pruning) as a <c>mAxisOrdinate</c> of the closed axis of its direction.
/// </summary>
public sealed record Dpm20HeaderRow(int HeaderId, int TableId, string Direction, bool IsKey);

/// <summary>
/// Row of <c>HeaderVersion</c> (read here only by <c>HeaderVID</c> already resolved via
/// <see cref="Dpm20TableVersionHeaderRow.HeaderVId"/>): <see cref="Code"/>/<see cref="Label"/>
/// for <c>mAxisOrdinate</c>/open <c>mAxis</c>, <see cref="PropertyId"/> for the dimension (of a
/// key header) or the metric (of a non-key header), and <see cref="ContextId"/> for the own
/// context of <c>mOrdinateCategorisation</c>. <see cref="SubCategoryVId"/> is the hierarchy
/// restriction of the open axis, resolved by the open-axis restriction loader.
/// </summary>
public sealed record Dpm20HeaderVersionRow(
    int HeaderVId, int HeaderId, string? Code, string? Label, int? PropertyId, int? ContextId, int? SubCategoryVId);

/// <summary>
/// Row of <c>ContextComposition</c> (the largest table of the source, with over 1.7 million rows
/// — read here ONLY by the <c>ContextID</c> values of the reached headers, never in full): the
/// (property, item) pair of a header's own context.
/// </summary>
public sealed record Dpm20ContextCompositionRow(int ContextId, int PropertyId, int ItemId);

/// <summary>
/// Row of <c>Cell</c>: the identity of the source cell, tied to <c>Table</c> (the ENTITY,
/// <see cref="TableId"/>). <see cref="ColumnId"/>/<see cref="RowId"/>/<see cref="SheetId"/> are
/// the <c>Header.HeaderID</c> of the X/Y/Z ordinate of THAT cell IN THE SOURCE (before the
/// transposition step and the full grid): an axis that the source does not have for that table
/// leaves the field <see langword="null"/>.
/// </summary>
public sealed record Dpm20CellRow(int CellId, int TableId, int? ColumnId, int? RowId, int? SheetId);

/// <summary>
/// Row of <c>TableVersionCell</c> (read here only by <c>TableVID</c>): whether the source cell
/// for that table version is excluded or void — the source of <c>mTableCell.IsShaded</c>: a cell
/// is shaded if the source does not supply it, or supplies it with <see cref="IsExcluded"/>.
/// <see cref="VariableVId"/>: the cell-to-variable link — <c>VariableVersion.ContextID</c> is the
/// ALREADY RESOLVED context of the data point, the source of <c>mOrdinateCategorisation</c>
/// (projected onto the ordinates). <see langword="null"/> for cells without a variable (shaded
/// cells).
/// </summary>
public sealed record Dpm20TableVersionCellRow(int TableVId, int CellId, bool IsExcluded, bool IsVoid, int? VariableVId);

/// <summary>
/// Row of <c>VariableVersion</c> (read here only by <c>VariableVID</c>): the COMPLETE and already
/// resolved context of a data point — <see cref="ContextId"/> is projected onto the ordinates to
/// derive <c>mOrdinateCategorisation</c>. The property (the metric) is NOT read here: it keeps
/// coming from the header (<c>Dpm20AxisAndCellLoader.BuildTablePlan</c>), unchanged.
/// </summary>
public sealed record Dpm20VariableVersionRow(int VariableVId, int? ContextId);

// ------------------------------------------------------------------------
// mOpenAxisValueRestriction — the bracket in the signature of an open axis.
// ------------------------------------------------------------------------

/// <summary>
/// Row of <c>SubCategoryVersion</c>: the validity window of ONE VERSION of a subcategory, whose
/// <see cref="SubCategoryVId"/> is literally the same value as
/// <see cref="Dpm20HeaderVersionRow.SubCategoryVId"/> — the hierarchy restriction of an open
/// axis. Versioned table: only <see cref="Dpm20AccessReader.ReadSubCategoryVersions"/> exposes
/// it, already filtered by the current-version predicate.
/// </summary>
public sealed record Dpm20SubCategoryVersionRow(
    int SubCategoryVId, int SubCategoryId, int StartReleaseId, int? EndReleaseId);

// ------------------------------------------------------------------------
// mHierarchyNode — the SubCategoryItem tree of the current version (or, when there is none, of
// the latest one) of each subcategory.
// ------------------------------------------------------------------------

/// <summary>
/// Row of <c>SubCategoryItem</c>: a candidate <c>mHierarchyNode</c> node, belonging to a concrete
/// version of a subcategory (<see cref="SubCategoryVId"/>). Note that <see cref="Label"/> is
/// EMPTY in all current rows: it is NOT the source of <c>HierarchyNodeLabel</c>, which comes from
/// <c>Item.Name</c> (<see cref="Dpm20ItemRow"/>). <c>Order</c> is a reserved word in Access SQL:
/// always use brackets when reading it.
/// </summary>
public sealed record Dpm20SubCategoryItemRow(
    int ItemId,
    int SubCategoryVId,
    int? Order,
    string? Label,
    int? ParentItemId,
    int? ComparisonOperatorId,
    int? ArithmeticOperatorId);

/// <summary>
/// Row of <c>Operator</c>: translates <c>ComparisonOperatorID</c>/<c>ArithmeticOperatorID</c> of
/// <c>SubCategoryItem</c> to <c>mHierarchyNode.ComparisonOperator</c>/<c>.UnaryOperator</c>.
/// Only <see cref="Symbol"/> is needed: neither <c>Name</c> nor <c>Type</c> has a target.
/// </summary>
public sealed record Dpm20OperatorRow(int OperatorId, string Symbol);
