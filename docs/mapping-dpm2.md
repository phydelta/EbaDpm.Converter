# Mapping: DPM 2.0 Access → distribution schema

This document describes how the DPM 2.0 pipeline fills each target table. DPM 2.0 is a new
metamodel (see [source-models.md](source-models.md)), but the target is the same 45-table
distribution schema as for DPM 1.0 ([target-schema.md](target-schema.md)). Rules that are
properties of the **target** — the data point signature algorithm, the semantics of axes and
cells, the special treatment of the metric — are shared with
[mapping-dpm1.md](mapping-dpm1.md) and only summarised here. Rules that depend on the
**source** are rebuilt from scratch.

Loaders run in this order (`src/EbaDpm.Converter.Core/Mapping/Dpm20/`):

| Loader | Target tables |
|---|---|
| `Dpm20TaxonomyDeriver` | (in memory) the derived taxonomies and their tables |
| `Dpm20SkeletonLoader` | `mRelease`, `mReportingFramework`, `mTaxonomy`, `mOwner`, `mOwnerParent`, `mLanguage`, `mTaxonomyPackage`, `mRewriteURI`, `aDatabaseProperties` |
| `Dpm20DictionaryLoader` | `mDomain`, `mDomainUnion`, `mMember`, `mHierarchy`, `mMetric`, `mDimension` |
| `Dpm20HierarchyNodeLoader` | `mHierarchyNode` |
| `Dpm20StructureLoader` | `mTemplateOrTable`, `mTable`, `mTaxonomyTable` |
| `Dpm20AxisAndCellLoader` | `mAxis`, `mTableAxis`, `mAxisOrdinate`, `mOrdinateCategorisation`, `mOpenAxisValueRestriction`, `mTableCell`, `mCellPosition` |
| `Dpm20ModuleLoader` | `mModule`, `mConceptualModule`, `mModuleBusinessTemplate` |
| `Dpm20ConceptLoader` | `mConcept`, `mConceptTranslation`, and every `ConceptID` column |

---

## Cross-cutting rules

| Rule | Detail |
|---|---|
| **Read as of the cutoff release.** | Versioned tables are filtered with `Start <= R AND (End IS NULL OR End > R)`, `EndReleaseID` exclusive. The dictionary is not filtered. See [source-models.md](source-models.md#the-cutoff-release-dpm-20). |
| **The source decides.** | Where the source has a value, it is exported; where it has none, the column stays empty. Nothing is invented to resemble another export of the format. |
| **One row per (table, taxonomy) pair.** | A table version that belongs to two derived taxonomies produces its structure twice, once per taxonomy, with fresh IDs. |
| **IDs are minted deterministically.** | DPM 2.0 has no entity matching axes, ordinates, cells or concepts one-to-one, so their IDs are counters assigned in a fixed order. |
| **`9999` is a literal.** | The "Open" sentinel domain and member, and the metric dimension `MET`, use ID `9999`, as in the distribution format; the member counter skips that value. |
| **Roots of `mTemplateOrTable` use parent `0`.** | Same convention as in DPM 1.0. Orphan `BusinessTable` nodes also use `0`. |
| **The DORA framework is excluded.** | Its tables and modules are not exported, and a table that belongs only to DORA is not emitted. DORA is not part of the reporting frameworks distributed in this format. |
| **Unresolved cases are reported, not forced.** | When a rule cannot decide (for example a dimension appearing on two axes that no source declares an owner for), the case is listed in the conversion log and left as is. |

---

## Skeleton

### mRelease

All rows of `[Release]`, not filtered by the selection.

| Column | Source |
|---|---|
| `ReleaseID`, `ReleaseCode` | `ReleaseID`, `Code` |
| `ReleaseDescription`, `Status` | `Description`, `Status` |
| `PublicationDate` | `Date`, copied as ISO text (never round-tripped through a date type, which would depend on the machine's time zone) |
| `IsCurrent` | `IsCurrent`, mapped directly; it changes from one publication to the next |
| `ConceptID` | minted (type `Release`) |

### mReportingFramework

`Framework` rows used by at least one selected taxonomy. `FrameworkCode` ← `Code`,
`FrameworkLabel` ← `Name`, without changing case.

### mTaxonomy

DPM 2.0 has no `Taxonomy` table; taxonomies are **derived** (`Dpm20TaxonomyDeriver`):

1. **Universe.** Table versions current at the cutoff that belong to a current module version
   (via `ModuleVersionComposition`). When a module declares an **abstract** table, its current
   concrete children are added to that module too, even if the composition does not list them:
   a module that declares an abstract table declares all its children.
2. **Framework of a table version.** `ModuleVersionComposition` → `ModuleVersion` →
   `Module.FrameworkID`. A table can belong to several frameworks.
3. **Emitted tables.** Abstract tables and tables that belong only to DORA are removed.
4. **Pairs.** Each emitted table version produces the taxonomy `(framework, cutoff release)` and
   the taxonomy `(framework, release in which this table version started)` — one pair if both
   coincide. Intermediate releases never produce a pair.
5. **Codes.** `TaxonomyCode` = framework code in lower case + space + release code
   (`corep 4.2`). `TaxonomyKey` = framework code in lower case + `/` + release (`corep/4.2`).

| Column | Value |
|---|---|
| `TaxonomyID` | synthetic, by `TaxonomyCode` ordinal order |
| `FrameworkID` | the framework |
| `TechnicalStandard` | framework code in lower case |
| `TaxonomyLabel`, `Version`, `PublicationDate`, `FromDate`, `ToDate`, `ExcelTemplate` | `NULL` — the source has no taxonomy entity. The framework name is not used as a label: it names the framework, not the taxonomy, and would lose the release. |

### Fixed rows

These tables hold identities of the distribution format, not data of the DPM model, and are
written as constants. (`Organisation` is **not** their source: its rows do not correspond to the
owners of the format.)

| Table | Content |
|---|---|
| `mOwner` | `eba` (European Banking Authority), `Technical`, `eu` (Eurofiling), with namespace, location, prefix and copyright |
| `mOwnerParent` | one row: EBA (1) → Eurofiling (3) |
| `mLanguage` | one row, `IsoCode = 'en'` |
| `mRewriteURI` | four rows (IDs 2–5) for `eba.europa.eu`, `w3.org`, `xbrl.org`, `eurofiling.info`, all in taxonomy package 1 |
| `aDatabaseProperties` | `Validation syntax version` = `2`, and `Source model` = `DPM 2.0` (the marker the validator reads) |

### mTaxonomyPackage

One row, derived from the cutoff release and the emitted frameworks:

| Column | Value |
|---|---|
| `Version` | `<cutoff release>.0.0` (e.g. `4.2.0.0`) |
| `Name` | `EBA XBRL <release> Reporting Frameworks <version> (<codes>)`, where `<codes>` are the emitted framework codes in upper case, sorted ordinally, joined with `_` |
| `Description` | `Name` + `. Requires Dictionary <release> or later` |
| `Identifier` | `http://www.eba.europa.eu/eu/fr/xbrl/tp/<release>/EBA_XBRL_<release>_Reporting_Frameworks_<version>.zip` |
| `PublicationDate` | `Release.Date` of the cutoff release |
| `SchemaLocation`, `Publisher`, `PublisherURL` | constants of the taxonomy package format |
| `LicenceName`, `LicenceHref` | empty strings; `Lang`, `PublisherCountry`, `CopyrightComment` are `NULL` |

The `Version`, `Identifier`, `Name` and `Description` patterns have been observed for a single
release and may need revisiting.

---

## Dictionary

The DPM 2.0 dictionary is built from categories (domains), items (members and properties) and
sub-categories (hierarchies). A **property** is an item of the special category `_PR`; it can act
as a metric, as a dimension, or as both (several dozen properties are both), so the two roles are
evaluated independently.

### mDomain

| Rows | Rule |
|---|---|
| One per `Category` | except `SE`, `_NA`, `_PR`, `_TE` (structural categories, not domains). IDs from 1 in `CategoryID` order. |
| `MET` | metric domain, label `Metric domain`, XBRL code `MET` |
| `INT_NA`, `STR_NA` | typed domains for typed dimensions whose values have no domain of their own (integer / non-empty string). Labels copied verbatim from the source definition. |
| `9999` | the `Open` sentinel, empty code |

`IsTypedDomain = NOT Category.IsEnumerated`; `DomainXBRLCode` = `eba_typ:` or `eba_exp:` + code;
`DomainDescription` = empty string; `IsNillable = 0`; `DataType` `NULL` (except the two synthetic
typed domains).

### mDomainUnion

A direct translation of `SuperCategoryComposition`: one row per (union domain, component
domain). The union is **not** materialised: the union domain keeps its own members.

### mMember

One row per `ItemCategory` row (the full history, so a renamed code such as `FGT` / `old-FGT`
produces both), except:

- `_TE` items (templates modelled as items) are skipped;
- `_PR` items (properties) become members of `MET` **only if they are metrics** (see below);
  other properties are dimensions, not members.

| Column | Rule |
|---|---|
| `MemberID` | sequential, skipping `9999` |
| `MemberCode` | `ItemCategory.Code` |
| `MemberLabel` | `Item.Name` |
| `MemberXBRLCode` | `eba_<domain code>:<code>`; metrics `eba_met:<code>` |
| `IsDefaultMember` | `ItemCategory.IsDefaultItem` |

Plus the sentinel `9999` (`Open`, domain 9999).

**Metric identification.** A property is a metric if any of these holds:
it is the property of a `fact` variable (`Variable.Type = 'fact'`); it is the property of a
non-key header (`Header.IsKey = 0`); or its code follows the metric naming convention
`<letter><letter><digits>` (for example `ei1511`, `si1036`). `Property.IsMetric` is not used: it
misses many properties that the model uses as metrics.

### mMetric

One row per `MET` member, in the same order, `CorrespondingMemberID` pointing to it.

| Column | Rule |
|---|---|
| `DataType` | from `DataType.Code` of the property: `i` Integer, `r` Decimal, `b` Boolean, `t` BooleanTrue, `d` Date, `e` Enumeration/Code, `m` Monetary, `p` Percent. For string types (`s`, `es`) the source code does not distinguish the two target types; the naming convention does: `String` if the property code matches `<letter><letter><digits>`, otherwise `NotEmptyString`. Unknown codes are reported and the metric is skipped. |
| `FlowType` | `STOCK` |
| `ReferencedHierarchyID`, `ReferencedDomainID` | the hierarchy of the first header (by `HeaderVID`) that uses the property with a `SubCategoryVID`, and that hierarchy's domain |
| `IsStartingMemberIncluded` | `1` when a hierarchy is referenced |
| `BalanceType`, `HierarchyStartingMemberID`, `IsAbstract`, `CustomDataTypeID` | `NULL` |

### mDimension

Dimension properties are those used in `ContextComposition` or as the property of a `key`
variable. Each produces **one row per `PropertyCategory` row** (the domain of its values over a
release window), crossed with the property code valid in the same window — so a property whose
domain or code changed between releases yields one dimension per period (`LEA` / `qLEA`).

| Column | Rule |
|---|---|
| `DimensionCode` | the property code valid in that window |
| `DimensionLabel` | `Item.Name` |
| `DimensionXBRLCode` | `eba_dim_<release>:<code>`, where `<release>` is the start of the overlapping window — the release in which this definition appeared |
| `DomainID` | the category of the `PropertyCategory` row; for `_NA` (typed values with no domain), `INT_NA` or `STR_NA` according to the property's data type (`i` / `s`) |
| `IsTypedDimension` | `IsTypedDomain` of that domain |
| `DimensionDescription` | empty string |

Plus the metric dimension: `DimensionID = 9999`, code and XBRL code `MET` (no release prefix),
label `Metric dimension`, domain `MET`. DPM 2.0 has no source row to rename, so it is synthesised.

### mHierarchy

`SubCategory`, one to one, IDs in source order. Sub-categories of `_PR` (metric hierarchies,
codes `AT*`) are assigned to the `MET` domain. `HierarchyDescription` = empty string.

### mHierarchyNode

The items of each sub-category's **effective version**: the version current at the cutoff or,
for a sub-category with no current version, its latest version (a hierarchy whose effective
version has no items gets no nodes).

| Column | Rule |
|---|---|
| `MemberID` | the item resolved to its `ItemCategory` row valid **in the release of the version being exported**. Resolution is global across categories, because hierarchies over union domains list items of the component domains. When an item resolves outside the hierarchy's domain but a member with the same code exists in that domain, that member is used. |
| `ParentMemberID` | `ParentItemID` resolved the same way; a parent that is not a node of the same sub-category stops the conversion |
| `Level`, `Path` | built by walking up the parents (`Path` with `.` as separator and terminator) |
| `HierarchyNodeLabel` | `Item.Name` (`SubCategoryItem.Label` is empty) |
| `ComparisonOperator`, `UnaryOperator` | `Operator.Symbol`; `UnaryOperator` is an empty string, not `NULL`, when there is none |
| `Order` | `SubCategoryItem.Order`, verbatim |
| `IsAbstract` | `0` |

---

## Templates and tables

### mTable and mTaxonomyTable

One row **per (taxonomy, table version) pair** of the selection. The same synthetic ID is used
for `mTable.TableID`, for the `BusinessTable` node in `mTemplateOrTable` and for
`mTaxonomyTable.AnnotatedTableID`, so `AnnotatedTableID = TableID` always.

| Column | Rule |
|---|---|
| `TableCode` | `TableVersion.Code` |
| `TableLabel` | `Code + ":" + Name` — the name is not trimmed |
| `XbrlFilingIndicatorCode` | the code of the table's **template**: its abstract parent table (`AbstractTableID`) if it has one, otherwise the table itself (`K_49.02.b` → `K_49.02`) |
| `JsonBlob` | empty BLOB |
| `FromDate`, `ToDate`, `XbrlTableCode`, `YDimVal`, `ZDimVal` | `NULL` |
| `mTaxonomyTable.IsSimplyReuse`, `IsTableSource` | `1` |

### mTemplateOrTable

Three strata. The tree exists only for taxonomies of the **cutoff release**; table nodes of
older taxonomies have parent `0`.

| Stratum | Type / `Level` | Source | Code / label |
|---|---|---|---|
| Group | `TableGroup` / 1 | an emitted `TableGroup` (see below) | `eba_tg` + normalised group code / group name |
| Template | `TableGroup` / 2 | the table's abstract parent, or the table itself | `eba_tg` + normalised template code / template name |
| Table | `BusinessTable` / 1 | the table version | `TableCode` / `TableLabel` |

- **Abstract tables are not emitted as tables, but they are the template nodes.**
- **Emitted groups.** `TableGroup` rows of type `templateGroup`, current at the cutoff, that
  contain at least one emitted table (over the whole derived universe, not only the selection)
  and whose set of emitted tables is not contained in another group's set (ties go to the lower
  `TableGroupID`). Redundant and DORA-only groups are thus dropped. The set is computed
  independently of the selection, so a taxonomy's tree does not change shape depending on what
  else is converted with it.
- **Parent of a template.** The emitted group that contains the template or any of its concrete
  children (`TableGroupComposition` never points to abstract tables).
- A group or template that serves two taxonomies gets one node in each.
- `Order`: `0` for groups; for templates and tables, 0-based **natural order** of the code
  within the parent (`C_2` before `C_10`). `Level` is a type classification, not the depth.
- `IsTableGroupSource = 1`; `TC`, `TT`, `TL`, `TD`, `YC`, `XC` are `NULL`.

---

## Axes, ordinates and cells

In DPM 2.0 a table version is a tree of **headers** (`TableVersionHeader` → `Header` /
`HeaderVersion`), each with a direction (`X`, `Y`, `Z`) and an `IsKey` flag. The structure of a
table version is computed once and instantiated, with fresh IDs, for every pair that uses it.

### Axes

```
For each direction X, Y, Z:
    non-key headers  -> one CLOSED axis of that orientation (after pruning, below)
    each key header  -> one OPEN axis with a single ordinate:
                            key header of direction X -> open Y axis   (transposition)
                            key header of direction Z -> open Z axis
                            key header of direction Y -> not supported, reported
```

**Pruning after transposition.** Once key headers are removed, an abstract header left without
any surviving non-key descendant is not emitted (applied bottom-up until nothing changes). The
order matters: pruning before transposition would keep empty abstract headers.

| Item | Rule |
|---|---|
| `mAxis.AxisLabel` | closed: `Columns` / `Rows` / `Sheets`; open: the key header label |
| `mAxis.AxisCode` | open axes only: the key header code; `NULL` on closed axes |
| `mAxis.OptionalKey` | `= IsOpenAxis` |
| `mTableAxis.Order` | closed `X`, `Y`, `Z`, then open `Y`, then open `Z` (by header), `1..n` per table |
| `mAxisOrdinate.Level`, `Order` | closed axes: depth from 1 and pre-order position from 1 (siblings by source order), over the pruned tree. Open axes: `0` and `0`. |
| `mAxisOrdinate.IsRowKey` | `1` only on the ordinate of an open axis |
| `IsDisplayBeforeChildren` | `0`; `TypeOfKey`, `RelatedDimensionTableId` `NULL` |
| Codes and labels | header code and label, trimmed |

### Cells

| Item | Rule |
|---|---|
| `mTableCell` | the **Cartesian product, axis by axis**, of the ordinates of the closed axes, abstract ordinates included. Open axes do not multiply cells: their single ordinate is a position of every cell. |
| `IsShaded` | `1` when the source has no `Cell` at that (column, row, sheet) coordinate, or has it with `TableVersionCell.IsExcluded`. |
| `IsRowKey` | `0` |
| `mCellPosition` | one row per axis of the table |
| `BusinessCode` | `{TableCode,<ordinate codes>}`, ordinate codes ordered by orientation **Y, X, Z** (row, column, sheet). When several open Y axes coexist, their codes go by descending axis ID — a declared convention, since the source has no field that fixes this order. |

`Cell.ColumnID` / `RowID` / `SheetID` are the header IDs of the cell's closed ordinates, so the
lookup "does the source have this cell?" uses only the closed axes.

### mOrdinateCategorisation

The categorisation of each **closed** ordinate is assembled from these sources, in order:

1. **Projection of the data point context.** Each cell's data point gives a fully resolved
   context (`TableVersionCell.VariableVID` → `VariableVersion.ContextID` → `ContextComposition`).
   For each ordinate and each dimension seen in the table: if all the ordinate's live cells agree
   on a member, that pair; if none of them carries the dimension, a **reset** to the domain's
   default member; if they disagree, nothing (another axis decides). An ordinate with no live
   cell at all falls back to the context declared on its own header (`HeaderVersion.ContextID`).
2. **One dimension, one axis.** If the projection places the same dimension on more than one
   closed axis, the axis whose header declares the dimension in its context keeps it; failing
   that, a dimension declared by the table context goes to `X`; otherwise the case is reported.
3. **Metric.** A non-key header with a `PropertyID` contributes `MET(<metric>)`. The metric is
   never inherited.
4. **Table context.** Pairs declared on `TableVersion.ContextID` are added to the ordinates of
   the axis that owns the dimension, where the ordinate has no value of its own.
5. **Closure.** The written rows are the **effective categorisation**: the parent's pairs
   inherited down the header tree, the ordinate's own pairs winning.

The single ordinate of an **open axis** gets `(dimension of the key header's property, 9999)`,
plus any fixed pairs declared on the key header's own context.

Other exports of the format store a minimised, delta-against-parent form of this table. Both
forms denote the same effective categorisation, so this table is not compared by row count.

`DPS` and `DimensionMemberSignature` hold the signature of each pair; they differ only for the
open pair of a restricted axis (hierarchy code versus hierarchy ID). `Source` is `NULL`.

### mOpenAxisValueRestriction

One row per open axis whose key header has a `HeaderVersion.SubCategoryVID`: the sub-category is
resolved through `SubCategoryVersion` to its `mHierarchy` row. If the referenced version is no
longer current at the cutoff (a sub-category re-versioned without updating every header that
used it), the version is resolved without the cutoff; the hierarchy is the same, and the case is
logged. `HierarchyStartingMemberID` is `NULL` and `IsStartingMemberIncluded` is `0`: DPM 2.0 does
not model a starting member.

### Signatures

`mTableCell.DPS` and `DatapointSignature` use **the same algorithm as DPM 1.0** (see
[mapping-dpm1.md](mapping-dpm1.md#data-point-signatures)): pairs collected from every ordinate
of the cell and its ancestors, nearest wins; default members dropped except the metric;
`MET(...)` first, then by dimension XBRL code in ordinal order, joined by `|`. Only non-shaded
cells get a signature. A restricted open axis is written `dim(*[HierarchyCode])` in `DPS` and
`dim(*[HierarchyID])` in the ID variant; without a starting member, the bracket has a single
part. `Context.Signature` in the source is not used: it serialises internal identifiers.

---

## Modules

| Table | Rule |
|---|---|
| `mModule` | Current `ModuleVersion` rows, excluding DORA modules and **document modules** (`Module.isDocumentModule`, links to PDF documentation without tables). `ModuleCode` = `ModuleVersion.Code` in **upper case**; `ModuleLabel` = `Name`. `TaxonomyID` = the cutoff-release taxonomy of the module's framework (a current module belongs to the current release). `XBRLSchemaRef` = `http://www.eba.europa.eu/eu/fr/xbrl/crr/fws/<framework>/<release>/mod/<module in lower case>.xsd`. `ConceptualModuleID` = `ModuleID`; `AutogenerateRefs = 1`; `JsonBlob` empty; `DefaultFrequency`, `JSONSchemaRef` `NULL`. |
| `mConceptualModule` | a 1:1 mirror of `mModule` |
| `mModuleBusinessTemplate` | one row per (module, emitted group containing any of the module's tables), via `ModuleVersionComposition` → `TableVersion` → `TableGroupComposition`. `BusinessTemplateID` is the level-1 group node of the module's taxonomy. `Order` is 0-based natural order of the group code — a declared convention (the source has no order for this relation). |

Unlike DPM 1.0, where `XBRLSchemaRef` had to be copied from the source, DPM 2.0 lets the
converter derive it.

---

## Concepts

DPM 2.0 has no `Concept` table; concepts are entities of the target, minted in a **final
phase** after all other loaders, by reading what they wrote. Tables are visited in a fixed order
(release, framework, taxonomy, domain, member, dimension, hierarchy, hierarchy node, axis,
ordinate, template-or-table, table, module) and, within each, by primary key; `ConceptID` is the
position in that sequence. This keeps concept IDs independent of loader internals.

| Item | Rule |
|---|---|
| `ConceptType` | one of the 13 types above (`Release`, `ReportingFramework`, `Taxonomy`, `Domain`, `Member`, `Dimension`, `Hierarchy`, `HierarchyNode`, `Axis`, `Ordinate`, `TemplateOrTable`, `Table`, `Module`) |
| `OwnerID` | `1` (EBA) |
| `ReleaseID` | the release suffix of the code the converter emitted: `DimensionXBRLCode` (`eba_dim_4.0:…`), `MemberXBRLCode` (same form), or `TaxonomyCode` (`corep 4.0`). `NULL` for every other type. |
| Dates | `NULL` |
| `mConceptTranslation` | one `label` row per concept with the label the entity already carries (`NULL` included), in language 1. No row for releases (no label column), the `MET` dimension and the `Open` member. No `description` rows: DPM 2.0 has no description texts for these entities. |

---

## Tables left empty

`vValidationRuleExpressions` and `vValidationRuleTables` (validation rules are out of scope,
even though the source contains the DPM-ML), and the tables with no DPM source:
`aContainerInfo`, `aDDSInfo`, `dFilingIndicator`, `dInstance`, `mConceptReference`,
`mCustomDataType`, `mNamespacePrefix`, `mReference`, `mReferencePart`, `mReferenceValue`,
`mResourceFile`, `mXbrlExportConfiguration`. See [target-schema.md](target-schema.md).
