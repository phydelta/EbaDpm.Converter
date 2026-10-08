# Mapping: DPM 1.0 Access → distribution schema

This document describes how the DPM 1.0 pipeline fills each target table. It is organised by
target table, in the order the loaders run:

| Loader (`src/EbaDpm.Converter.Core/Mapping/`) | Target tables |
|---|---|
| `SkeletonLoader` | `mOwner`, `mOwnerParent`, `mLanguage`, `mReportingFramework`, `mTaxonomy`, `mRelease`, `mConcept` (part) |
| `DictionaryLoader` | `mDomain`, `mMember`, `mDimension`, `mMetric`, `mHierarchy`, `mHierarchyNode`, `mConceptTranslation` (part) |
| `TemplateOrTableLoader` | `mTemplateOrTable`, `mTable`, `mTaxonomyTable` |
| `AxisAndCellLoader` | `mAxis`, `mTableAxis`, `mAxisOrdinate`, `mOrdinateCategorisation`, `mOpenAxisValueRestriction`, `mTableCell`, `mCellPosition` |
| `ModuleLoader` | `mModule`, `mConceptualModule`, `mModuleBusinessTemplate` |

The source model is described in [source-models.md](source-models.md) and the target tables in
[target-schema.md](target-schema.md). The DPM 2.0 counterpart of this document is
[mapping-dpm2.md](mapping-dpm2.md).

The conversion is not a table-by-table copy. Four model transformations run through it:

1. **Data points become signatures.** The target has no data point entity: each cell carries a
   signature string that encodes its full dimensional categorisation (computed, see
   [Signatures](#data-point-signatures)).
2. **Open axes are transposed.** Key columns of the Access X axis become open Y axes of their
   own, and an open Z axis is split into one axis per open dimension.
3. **Lookups are denormalised.** Data type, flow type and similar IDs are written as text.
4. **The template tree is rebuilt** from table groups, templates and table versions.

---

## Cross-cutting rules

| Rule | Why |
|---|---|
| A missing boolean is written as `0`; a missing foreign key is written as `NULL`. | `0` in a foreign key would be a dangling reference. |
| The root of a tree has parent `NULL` (ordinates, hierarchy nodes) — except in `mTemplateOrTable`, whose roots use `0`. | Each table follows the convention of the distribution format. |
| Every emitted object that has a concept has its concept in `mConcept`, and every concept describes an emitted row. | A dangling concept is incoherent. |
| XBRL codes from the Access file are written **verbatim**, except where the target format *constructs* them (`mDomain.DomainXBRLCode`, the metric domain and dimension). | The XBRL codes are the identity of the concepts; only codes that the format itself composes are rebuilt. |
| ID `9999` is reserved in `mDomain`, `mDimension` and `mMember` for the "Open" sentinel of the format. If the source uses it, the source object is renumbered. | The sentinel must be recognisable by its ID. |
| Synthetic IDs are assigned in a deterministic order. | Two runs on the same input produce the same content. A final `VACUUM` makes file size stable as well. |
| Objects are recognised **by their role, not by their name**. | The Access file and the target use different names for the same things (`ATY` vs `MET`, `999` vs `9999`, `TableVersion` vs `Table`, `eba_` vs `eba_exp:`). Looking in the source for the target's name finds nothing. |

### What gets exported for a selection

- **The shared part, always complete:** owners and the whole dictionary (domains, members,
  dimensions, metrics, hierarchies, hierarchy nodes, translations). The dictionary is a shared
  vocabulary; filtering it would make outputs impossible to reuse or compare across taxonomies.
- **The explicit part, filtered:** the selected taxonomies and their structure (frameworks,
  modules, templates, tables, axes, ordinates, categorisations, cells, positions and
  restrictions). Frameworks are filtered too: every emitted framework is used by an emitted
  taxonomy.

---

## Skeleton

### mOwner, mOwnerParent, mLanguage

| Table | Rule |
|---|---|
| `mOwner` | Row 1 from Access `Owner` (the EBA), with `OwnerCode` = `OwnerPrefix` (the Access file has no separate code column). Rows 2 (`Technical`) and 3 (`Eurofiling`, code `eu`) are fixed identities of the distribution format, emitted as constants; their `OwnerLocation` is set to their namespace. |
| `mOwnerParent` | One constant row: owner 1 (EBA) has parent 3 (Eurofiling). |
| `mLanguage` | One row: `LanguageID = 1`, `IsoCode = 'en'`. The distribution format carries English only. |

### mReportingFramework

From `ReportingFramework`, restricted to frameworks of the selected taxonomies. Code and label
verbatim.

### mTaxonomy

From `Taxonomy`, selected rows only.

| Column | Source |
|---|---|
| `TaxonomyCode` | `Taxonomy.TaxonomyCode` in **lower case** |
| `TaxonomyLabel`, `TechnicalStandard` | verbatim |
| `Version` | `DpmPackageCode` |
| `PublicationDate` | `ActualPublicationDate` |
| `FromDate`, `ToDate` | from the taxonomy's `Concept` (the `Taxonomy` table has no such columns) |
| `ExcelTemplate` | `NULL` (no source) |

### mRelease

The Access file has no release table with IDs or dates, so releases are rebuilt from the
calendar of **all** taxonomies (not only the selected ones):

- the date of a release is the earliest `ActualPublicationDate` of any taxonomy with that
  `DpmPackageCode`;
- the emitted releases go from a **floor** upwards, ordered by date, numbered `1..n`;
- the floor is release `3.4`, or the oldest release among the selected taxonomies if that is
  older. The floor exists so that every release referenced by a concept (including release
  suffixes in dimension codes) has a row to point to.

`IsCurrent = 1`; `ReleaseDescription`, `Status` and `ConceptID` are `NULL` (no source).

### mConcept (skeleton part)

Concepts of the emitted frameworks and taxonomies, copied from `Concept`. A taxonomy concept
gets the `ReleaseID` of its own `DpmPackageCode`; other concepts get `NULL` here. Later loaders
add the concepts of the objects they emit (see [Concepts](#concepts)).

---

## Dictionary

### mDomain

From `Domain`, with `DataTypeID` resolved to the data type label.

| Column | Rule |
|---|---|
| `DomainXBRLCode` | **Constructed**: `eba_exp:` + `DomainCode` for an explicit domain, `eba_typ:` + `DomainCode` for a typed domain. The Access column holds a different form (`eba_AP`). |
| `IsNillable` | `NULL` (no source) |
| Metric domain | Written as code `MET`, label `Metric domain`, XBRL code `MET` (the Access file calls it `AT` / `Metric` / `eba_met`). It is identified **by role**: the only domain whose member set equals the set of `Metric.MetricID`. If zero or several domains match, the conversion stops. |
| Sentinel | One extra row `DomainID = 9999`, label `Open`, empty code and XBRL code. If a real domain already uses 9999 the conversion stops (no renumbering rule exists for domains). |

### mMember

From `Member`, XBRL codes verbatim (no release suffix).

- The Access "open value" sentinel (`MemberID = 999`, code `x999`, label `<Key value>`, no
  domain) is **not** emitted: it is consumed when categorisations are translated.
- One extra row is added for the target sentinel: `MemberID = 9999`, domain 9999, label `Open`,
  empty code, `NULL` XBRL code, with a synthetic concept.
- If a real member uses `MemberID = 9999`, it is renumbered to `MAX(MemberID) + 1`. The same
  translation is applied everywhere the member appears (hierarchy nodes, including inside
  `Path`, and categorisations).

Note the two sentinels are different: **999** in the Access file, **9999** in the target.
Searching the Access file for 9999 finds an ordinary NACE member and no open dimensions at all.

### mDimension

From `Dimension`, XBRL codes verbatim.

- `IsTypedDimension` is **derived**: the `IsTypedDomain` of the dimension's domain.
- The metric dimension (`ATY` in the Access file) is identified by role — the dimension whose
  domain is the metric domain — and cross-checked as the only dimension with a `NULL` XBRL code.
  It keeps its ID and concept and is **renamed** to code `MET`, label `Metric dimension`, XBRL
  code `MET`.
- `DefaultMemberID` is `NULL`.

### mMetric

From `Metric` (a metric *is* a member: `MetricID` = `Member.MemberID`).

| Column | Rule |
|---|---|
| `CorrespondingMemberID` | `= MetricID` |
| `DataType`, `FlowType` | resolved to labels |
| `ReferencedDomainID` | `CodeDomainID` |
| `ReferencedHierarchyID` | `CodeSubdomainID` |
| `BalanceType`, `HierarchyStartingMemberID`, `IsStartingMemberIncluded`, `IsAbstract`, `CustomDataTypeID` | `NULL` (no reliable source) |

### mHierarchy

From `Hierarchy`, verbatim. The code keeps the Access casing.

### mHierarchyNode

From `HierarchyNode`, with the tree **normalised**:

- `Path` is used as is when it is well formed (ends with the node's own `MemberID`, dot as
  separator and terminator: `3677.1073.1042.`); otherwise it is rebuilt by walking up
  `ParentMemberID`.
- `Level` and `ParentMemberID` are always **derived from the final `Path`**, never copied.
- A root that points to itself is treated as a root. A `ParentMemberID` that is not a node of
  the same hierarchy **stops the conversion**: silently re-rooting it would graft a subtree in
  the wrong place.
- `HierarchyNodeID` is synthetic, in `(HierarchyID, MemberID)` order. `Order`, operators and
  `IsAbstract` are verbatim. `HierarchyNodeLabel` is `NULL` (no source column); its translation
  uses the label of the member it represents.

---

## Templates and tables

### mTemplateOrTable

A three-level tree. Codes of group nodes are built as `eba_tg` + normalised code, where
normalisation turns whitespace into `_`, removes en and em dashes (`–`, `—`) and keeps the
ordinary hyphen.

| Level | Type / `Level` | Source | Parent | `Order` |
|---|---|---|---|---|
| 1 | `TableGroup` / 1 | `TableGroup` of the selected taxonomies | `0` (root) | `0` |
| 2 | `TableGroup` / 2 | `Template`, one node per distinct `(TaxonomyID, TemplateID, TableGroupID)` in `TaxonomyTableVersion` | its level-1 group | 0-based, ordinal by code within the parent |
| 3 | `BusinessTable` / 1 | `TableVersion`, one node **per `(TaxonomyID, TableVID)`** | its level-2 template in the same taxonomy, or `0` if unresolved | 0-based, ordinal by code within the parent |

- `Level` is a classification by node type, **not** the depth in the tree.
- `TemplateGroup` does not feed this tree.
- A table version reused by several taxonomies gets one `BusinessTable` node in each, under that
  taxonomy's own template, so the tree of every taxonomy is complete. All those nodes share the
  `TableVersion` concept.
- `BusinessTable` code = `XbrlTableCode`; label = `XbrlTableCode + ":" + TableVersionLabel`.
- `IsTableGroupSource = 1`; `TC`, `TT`, `TL`, `TD`, `YC`, `XC` are `NULL`.

### mTable

One row per **distinct** `TableVID` (the primary key is a single column), with a synthetic
`TableID`.

| Column | Source |
|---|---|
| `TableCode` | `TableVersion.XbrlTableCode` |
| `TableLabel` | `XbrlTableCode + ":" + TableVersionLabel` |
| `XbrlFilingIndicatorCode`, `FromDate`, `ToDate` | `TableVersion`, verbatim |
| `ConceptID` | `TableVersion.ConceptID` |
| `XbrlTableCode`, `YDimVal`, `ZDimVal` | `NULL` |
| `JsonBlob` | empty BLOB (not `NULL`) |

Reuse across taxonomies is expressed in `mTaxonomyTable` instead of duplicating the table row,
which keeps the information that the Access model records (one physical table version shared by
up to 21 taxonomies).

### mTaxonomyTable

One row per `(TaxonomyID, TableVID)` of `TaxonomyTableVersion`. `AnnotatedTableID` points to the
`BusinessTable` node of that taxonomy. `IsSimplyReuse = 1` and `IsTableSource = 1` (constants of
the 4.x format; the Access `IsSimpleReuse` column is not what this flag means).

---

## Axes, ordinates and cells

### The transformation of axes

```
Every table:
    axes with orientation 'O' are dropped (they never have ordinates or labels;
    if one ever does, the conversion stops)

Closed table:
    each Access axis -> one mAxis with the same orientation and the same ID

Open table:
    X axis:   ordinates with IsRowKey = 0  -> stay on the X axis
              ordinates with IsRowKey = 1  -> EACH becomes its own open Y axis with a single
                                              ordinate (same OrdinateID, code and label)
    open Y axis of the Access file (sentinel ordinate '999 ')  -> disappears
    open Z axis  -> split into N open Z axes, one per open dimension of its sentinel,
                    ordered by DimensionID, ordinate codes renumbered 0010, 0020, ...
```

A table never has more than one Access axis per orientation, and an open X axis is not
supported (the conversion stops if it appears).

### mAxis

| Column | Rule |
|---|---|
| `AxisID` | the Access ID for closed axes and key-column Y axes; synthetic (above the Access maximum) for split Z axes |
| `AxisOrientation` | `X`, `Y` or `Z` |
| `AxisLabel` | closed axis: the Access label if present, else `Columns` / `Rows` / `Sheets`; open axis: the label of its single ordinate (for a split Z axis with several dimensions, the dimension label) |
| `AxisCode` | `NULL` on closed axes; the code of the single ordinate on open axes |
| `IsOpenAxis`, `OptionalKey` | `OptionalKey = IsOpenAxis` |
| `ConceptID` | minted (type `Axis`) |

### mTableAxis

One row per axis (axes are never shared between tables). `Order` is a dense `1..n` per table,
by class — closed `X` < closed `Y` < closed `Z` < open `Y` < open `Z` — and, within a class, by
ascending `DimensionID` of the open dimension. (`Axis.AxisOrder` is always empty in the source;
if it is ever populated the conversion stops so the rule can be revisited.)

### mAxisOrdinate

| Column | Rule |
|---|---|
| `OrdinateCode` | Access code, **trimmed** (the Access column is padded to 4 characters) |
| `OrdinateLabel` | the literal label of the ordinate, trimmed |
| `ParentOrdinateID`, `Level` | from `ParentOrdinateID` only; the Access `Path` is never read (it is stale in some rows) |
| `Order` | **pre-order traversal** of the transformed axis, siblings by Access `Order`, `1..n` per axis |
| Open-axis ordinate | `Level = 0`, `Order = 0`, `IsRowKey = 1`, no parent (it belongs to no tree) |
| `IsDisplayBeforeChildren` | `0` |
| `TypeOfKey`, `RelatedDimensionTableId` | `NULL` |
| `ConceptID` | minted (type `Ordinate`) |

### mOrdinateCategorisation

The Access file stores, on each ordinate, the **full closure** of its categorisation. The
converter emits that closure plus explicit resets, which is correct whether the consumer
inherits categorisations down the ordinate tree or not:

```
For each CLOSED ordinate O with parent P:
    emit (O, D, M)                   for every categorisation the Access file has on O
    emit (O, D, default(domain(D)))  for every dimension that P has and O does not   <- reset
    the metric dimension is excluded from resets: it is never inherited

For each open axis (sentinel S, dimension D):
    emit (ordinate of (S, D), D, 9999)
    plus the fixed (non-open) categorisations of S, moved to the new ordinate when S maps to
    exactly one open axis, or to the first one when S splits into two axes with a single fixed
    dimension; other shapes are reported and left out
```

If a dimension's domain has no default member the reset is impossible; it is skipped and
counted in the conversion report. A minimisation step that would drop pairs redundant with the
parent exists as an isolated extension point and is currently the identity: other
implementations of the format store a minimised, delta-against-parent form, and the two forms
denote the same effective categorisation. Comparisons of this table must therefore be made on
the **effective closure**, not row by row.

`Source` is `NULL`. `DPS` and `DimensionMemberSignature` hold the signature of the pair (see
below).

### mOpenAxisValueRestriction

Not read from the Access `OpenAxisValueRestriction` table (an ambiguous aggregate). The link
comes from the `RestrictionID` on the sentinel ordinate's categorisation for the axis's
dimension, and the content from `OpenMemberRestriction`:

| Column | Rule |
|---|---|
| `HierarchyID` | `OpenMemberRestriction.HierarchyID` (no row if `NULL`, or if the axis is open without restriction) |
| `HierarchyStartingMemberID`, `IsStartingMemberIncluded` | `MemberID` and `MemberIncluded`, unless `IgnoreMemberID` is set, in which case `NULL` |

### mTableCell and mCellPosition

| Item | Rule |
|---|---|
| Cells | Every `TableCell` of the emitted tables, **except** cells positioned on a key column (those columns became open axes). |
| `IsShaded` | verbatim |
| `IsRowKey` | `0` |
| `BusinessCode` | **translated** from `TableCell.CellCode`, not recomposed: `{R 22.03, r0010, c0010}` → `{R_22.03,0010,0010}` (spaces in the table code become `_`, the `r`/`c`/`s` prefixes are dropped, and `999` coordinates of open axes are dropped). Part of the business codes are assigned by the business templates rather than calculated, so no recomposition from ordinates reproduces all of them. |
| `mCellPosition` | Mechanical consequence of the axis transformation: one row per closed ordinate of the cell; a sentinel position expands into the ordinates of all open axes it became. |

## Data point signatures

Each non-shaded cell gets two signatures computed from its ordinates; a shaded cell gets
`NULL` (not an empty string).

```
DPS        := MET( metricXbrl ) [ "|" pair ]*
pair       := dimXbrl "(" value ")"
value      := memberXbrl                                         -- explicit
            | "*"                                                -- open, unrestricted
            | "*[" hierarchy "]"                                 -- open, restricted to a hierarchy
            | "*[" hierarchy ";" startMember ";" included "]"    -- restricted with a starting member
```

Algorithm, per cell:

1. **Collect with inheritance.** For each ordinate of the cell, walk up `ParentOrdinateID`. For
   each dimension, the pair found at the **smallest depth** (the nearest ordinate) wins.
2. **Drop default members, except the metric.** Pairs whose member is the domain default are
   omitted, but the metric pair is always kept (metric members are often flagged as defaults).
3. **Compose.** `MET(...)` first; the remaining pairs sorted by dimension XBRL code with
   **ordinal** comparison; joined with `|`.

Two variants are produced and composed independently:

| Ordinate column | Cell column | Content |
|---|---|---|
| `mOrdinateCategorisation.DPS` | `mTableCell.DPS` | XBRL codes |
| `mOrdinateCategorisation.DimensionMemberSignature` | `mTableCell.DatapointSignature` | database IDs |

They differ only in the brackets of restricted open axes: `*[BT3]` (hierarchy code; starting
member code) in `DPS` versus `*[31]` (hierarchy ID; starting member ID) in the ID variant. The
ID variant encodes this database's IDs, so it can only be compared with another database after
normalising those numbers.

A `RestrictionID` attached to a *metric* categorisation restricts the metric's values, not an
axis, and never produces brackets.

---

## Modules

| Table | Rule |
|---|---|
| `mModule` | From `Module` of the selected taxonomies, synthetic `ModuleID` in `(TaxonomyID, ModuleCode)` order. Code, label and `XBRLSchemaRef` verbatim. `DefaultFrequency` and `JSONSchemaRef` `NULL`; `AutogenerateRefs = 1`; `JsonBlob` empty BLOB. |
| `mConceptualModule` | A 1:1 mirror of `mModule` (same ID, code and label). Not taken from the Access `ConceptualModule` table, and it has no concept of its own. |
| `mModuleBusinessTemplate` | For each module, the level-1 `TableGroup` nodes of its tables: `ModuleTableVersion` gives the module's table versions, `TaxonomyTableVersion` (filtered to the module's taxonomy) gives their `TableGroupID`, which maps to the level-1 node with the same `eba_tg` code. Groups are de-duplicated per module; `Order` is 0-based, ordinal by node code. The Access `ModuleTableOrGroup` table is not used because it is incomplete (some modules with tables have no rows there). |

---

## Concepts

`mConcept` rows are copied from the Access `Concept` table for every emitted object, with these
rules:

- **`ConceptType` is translated to the target vocabulary**: `TableVersion` → `Table`;
  `TableGroup` and `Template` → `TemplateOrTable`. The other types used (`Axis`, `Ordinate`,
  `Dimension`, `Domain`, `Hierarchy`, `HierarchyNode`, `Member`, `Module`,
  `ReportingFramework`, `Taxonomy`) already match.
- The concepts of the Access `Table` entity are not emitted: that entity produces no target row.
- Axis and ordinate concepts are **minted** (above the highest concept ID already written), with
  types `Axis` / `Ordinate`, because the Access concepts for those objects carry a different
  type.
- `ReleaseID` is set only where the release is known: for taxonomies (their own package) and
  for dimensions and members whose XBRL code prefix carries a release suffix
  (`eba_dim_4.0:EXC` → release `4.0`). Everything else is `NULL`.
- Sharing a concept between rows that describe the same business object (a table and its
  `BusinessTable` nodes, hierarchy nodes of the same member) is accepted.

`mConceptTranslation` is **synthesised**: the Access translation table is empty, and the texts
live in the `*Label` and `*Description` columns of each entity. For every emitted concept, a
`label` row (language 1) carries the same label that the entity row carries, and a
`description` row is added when a description exists; empty texts produce no row. Hierarchy nodes use the label
of their member. When two objects share a concept, the first translation written wins.
