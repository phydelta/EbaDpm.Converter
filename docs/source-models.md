# Source models

The converter reads the EBA **DPM Database** published as a Microsoft Access file (`.accdb`).
The EBA has published that database in two structurally different data models, and the
converter supports both:

| Model | Typical files | Releases covered | Pipeline |
|---|---|---|---|
| **DPM 1.0** | `DPM 1.0 Database_v4_1_20250709.accdb` | Up to release **4.1** (the last DPM 1.0 publication) | `Access/Dpm10AccessReader` + `Mapping/*Loader` |
| **DPM 2.0** | `DPM2 Database_v 4_2_1.accdb`, `DPM2 Database_v 4_3_20260622.accdb` | Release **4.2** onwards | `Access/Dpm20/Dpm20AccessReader` + `Mapping/Dpm20/*` |

Both pipelines write the **same** 45-table SQLite distribution schema (see
[target-schema.md](target-schema.md)). The distribution format was originally designed around
DPM 1.0, so for DPM 1.0 the mapping is mostly a reshaping of existing entities; DPM 2.0 is a new
metamodel and many target entities have to be *derived* rather than copied.

The two pipelines are deliberately separate: they share the output schema, the CLI, the
taxonomy selector and a few helpers (code normalisation, the data point signature algorithm),
but not their readers or their mapping rules.

Specifications used as background (third-party documents, not redistributed here):

- *EBA CRD IV DPM Database Description* (v2.1) — the DPM 1.0 source model.
- *DPM Metamodel Documentation* and *DPM metamodel* (ER diagram) of the DPM 2.0 "Refit" project —
  the DPM 2.0 source model.
- *EIOPA DPM Database Technical Documentation* — the column-by-column semantics of the target
  schema (the EBA layout is a strict subset of the EIOPA one).

---

## DPM 1.0

DPM 1.0 is the classic DPM model: a dictionary of domains, members, dimensions and metrics,
and a table model of axes, ordinates and cells, with explicit data points.

### Main tables

| Area | Access tables |
|---|---|
| Ownership and calendar | `Owner`, `Concept`, `DpmPackage`, `ReportingFramework`, `Taxonomy` |
| Dictionary | `Domain`, `Member`, `Dimension`, `Metric`, `DataType`, `FlowType`, `Hierarchy`, `HierarchyNode` |
| Table structure | `TableGroup`, `Template`, `Table`, `TableVersion`, `TaxonomyTableVersion` |
| Axes and cells | `Axis`, `AxisOrdinate`, `OrdinateCategorisation`, `OpenMemberRestriction`, `TableCell`, `CellPosition` |
| Modules | `Module`, `ModuleTableVersion` |
| Not used | `DataPoint`/`DataPointVersion`/`ContextDefinition` (the target encodes data points as signature strings instead), validation rules (`ValidationRule`, `Expression`, …), `TemplateGroup`, `ConceptualModule`, `ModuleTableOrGroup`, `OpenAxisValueRestriction` |

Characteristics that shape the mapping (details in [mapping-dpm1.md](mapping-dpm1.md)):

- Every object carries a `ConceptID` into a shared `Concept` table, which the converter copies.
- One database holds **every** published taxonomy (128 in release 4.1), each tied to a
  `DpmPackage` code (the DPM release). The user selects which taxonomies to export.
- The Access file has no release table with IDs or dates: release dates come from
  `Taxonomy.ActualPublicationDate`.
- Open (variable-length) tables are modelled with key columns on the X axis and a sentinel
  ordinate (`OrdinateCode = '999 '`) on the open axis; the "open value" sentinel member is
  `MemberID = 999`.

## DPM 2.0

DPM 2.0 is a new metamodel. The dictionary is expressed as **categories**, **items** and
**properties**; tables are made of **headers** rather than axes and ordinates; and every
versioned entity carries a validity window in releases instead of dates.

### Main tables

| Area | Access tables |
|---|---|
| Calendar and frameworks | `Release`, `Framework`, `Organisation` (not used) |
| Dictionary | `Category`, `Item`, `ItemCategory`, `Property`, `PropertyCategory`, `DataType`, `SuperCategoryComposition`, `SubCategory`, `SubCategoryVersion`, `SubCategoryItem`, `Operator` |
| Contexts and variables | `ContextComposition`, `Variable`, `VariableVersion` |
| Table structure | `Table`, `TableVersion`, `TableGroup`, `TableGroupComposition` |
| Headers and cells | `Header`, `HeaderVersion`, `TableVersionHeader`, `Cell`, `TableVersionCell` |
| Modules | `Module`, `ModuleVersion`, `ModuleVersionComposition` |
| Validation rules | `Operation`, `OperationVersion`, `OperationVersionData`, `OperationNode`, `OperationScope`, `OperationScopeComposition`, `OperationOutput`, `OperationCodePrefix`, `OperandReference`, `OperandReferenceLocation`, `Operator`, `OperatorArgument`: not used by the 45-table output (its `vValidationRule*` tables stay empty); copied complete by the native output ([target-schema-native.md](target-schema-native.md)) |

Differences from DPM 1.0 that matter to the converter (details in
[mapping-dpm2.md](mapping-dpm2.md)):

- **There is no `Taxonomy` table.** Taxonomies are derived from frameworks, releases and the
  validity windows of table versions.
- **The 45-table mapping does not use the `Concept` table.** All concepts of that output are minted by the converter. (The 4.4 database does have a `Concept` table, `ConceptGUID`, `ClassID`, `OwnerID`, 837,042 rows, measured 2026-11-25; the native output copies it.)
- **There is no separate metric entity.** Metrics and dimensions are both *properties*; which
  role a property plays is decided from how the model uses it (and a property can be both).
- **Axes and ordinates are a single entity (`Header`).** Axes are rebuilt from header direction
  and the `IsKey` flag.
- **Everything is versioned by release.** Versioned tables have `StartReleaseID` and
  `EndReleaseID`, and a DPM 2.0 database contains the full history of every release it covers.

### Releases in each publication

| File | Releases in `[Release]` | Cutoff derived |
|---|---|---|
| `DPM2 Database_v 4_2_20251125.accdb` | 3.4, 3.5, 4.0, 4.1, 4.2 | 4.2 |
| `DPM2 Database_v 4_2_1.accdb` (2026-02-27) | the above plus 4.2.1 | 4.2.1 |
| `DPM2 Database_v 4_3_20260622.accdb` | the above plus 4.2.1, 4.2.1.1, 4.2.1.2, 4.3 | 4.3 |
| `DPM2 Database_v 4_4_20261006.accdb` | the above plus 4.3.0.1, 4.4 | 4.4 |

The `ReleaseID` numbering is not stable between publications (release 4.3 uses IDs in the
`1010000xxx` range while 4.2 used 1–5), so the converter never relies on ID ranges or positions —
only on the maximum ID and on `Release.Code`.
### Catalogue of the 4.4 database

Measured on `DPM2 Database_v 4_4_20261006.accdb` on 2026-11-25; figures of one publication, not
of the format (the physical schema changes between publications, see below).

- **70 user tables**, no saved queries. 59 are copied by the native output and 11 are excluded
  (change log and EBA working tables, [target-schema-native.md](target-schema-native.md#content)).
- **Column types** (all 70 tables): long integer 243, text 132, GUID 51 (`RowGUID` ×47,
  `ConceptGUID` ×4), Yes/No 46, decimal 3, date 3, byte 1.
- **Keys**: every user table has a primary key except `ModelViolations`, `VarGeneration_Detail` and
  `VarGeneration_Summary` (all excluded); 21 primary keys are composite (18 in the exported tables).
- **Relationships**: 178 between user tables, all on one column, all `ON UPDATE CASCADE`.
- **Largest tables**: `ContextComposition` 1,757,180 rows, `Concept` 837,042, `OperandReference`
  623,060, `OperandReferenceLocation` 588,878, `TableVersionCell` 407,898, `VariableVersion`
  299,573.
- **Empty tables**: `ChangeLog`, `ChangeLogAttribute`, `Document`, `DocumentVersion`, `Language`,
  `Reference`, `Role`, `Subdivision`, `SubdivisionType`, `Translation`, `User`, `UserRole`,
  `OperationCodePrefix`, `OperationOutput`.

**The physical schema changes between publications.** From 4.2.1 to 4.4: booleans went from
`SMALLINT` to `BIT` (all 46 columns); `ATTT2Hierarchies`, `PSCurrentItemCategory`,
`PSCurrentPropertyCategory` and `VariableCalculation` disappeared; `OperationOutput` appeared;
`TableAssociation` gained `StartReleaseID` / `EndReleaseID`; `OperandReference.OperationVID`,
`OperationVersion.OutputVariableID` and `Organisation.URL` were added. The 4.3 file also shipped
two leftover temporary tables (`~TMPCLP531411`, `~TMPCLP571251`). This is why the native output
builds its DDL from the catalogue of the database it converts, against the 4.4 baseline.

A later publication reproduces its earlier releases: every release it contains can be converted
from it (see [The release of a file](#the-release-of-a-file-dpm-20)). On
`DPM2 Database_v 4_2_1.accdb`, the rows in force at 4.2 are the same in number as in
`DPM2 Database_v 4_2_20251125.accdb` (53 module versions, 929 table versions, 19843 header
versions, 117993 variable versions; measured 2026-10-08), while 3 table versions end at 4.2.1.
The 2025-11-25 edition is no longer published by the EBA. Converting the 4.4 database as of 4.3
gives the same structure as converting the 4.3 database (taxonomies, modules, tables, axes,
ordinates, categorisations, cells and restrictions identical by business key; measured
2026-11-16); the remaining differences are corrections the EBA made to earlier data in 4.4
(labels, release status).

---

## How the source model is detected

Detection happens once, at the start of every command that reads an `.accdb`
(`Access/SourceModelDetector.cs`). It looks only at the **catalogue** of user tables
(`OleDbConnection.GetSchema("Tables")`, `TABLE_TYPE = 'TABLE'`); no row of data is read to decide.

| Catalogue contains | Model |
|---|---|
| a table named `Domain` | **DPM 1.0** (wins even if `Category` also exists) |
| a table named `Category` and no `Domain` | **DPM 2.0** |
| neither | error — the file is not a DPM database the converter knows; no model is assumed |

Table names are compared case-insensitively. Requiring `Category` *and* the absence of `Domain`
keeps older DPM 1.0 schemas (for example the 2014 *DPM Database 2.1.0.1*) from being
misclassified by a single-table test. The tests check the rule on synthetic `.accdb` files built
with each combination of tables.

If the file cannot be opened as an Access database at all (wrong path, a `.db` file, a
truncated file), the provider error is re-raised with the file path and the expected formats,
keeping the original provider message as the inner exception.

The detected model is always written to the console output, and DPM 2.0 outputs additionally
record it in `aDatabaseProperties` (`'Source model' = 'DPM 2.0'`). The validator uses that row
to decide which rule set applies; DPM 1.0 outputs leave the row absent so their content is
unchanged.

## The release of a file (DPM 2.0)

A DPM 2.0 database contains several releases at once. The converter writes **one file per
release R**, and every read for that file is made **as of R**: the file holds the module versions
published in R, with the structure and dictionary they had in R.

**Choosing R.** `Dpm20AccessReader` takes the release to read as of through its constructor
(by `Release.Code`, or by `ReleaseID` in tests); without one it uses the row of `[Release]` with
the highest `ReleaseID`, which in every published file is also the most recent release. Internally
the reader calls R the *cutoff*. The CLI converts every release that publishes at least one
module version, or the one given with `--release`, and logs each release it writes or skips.

**Validity predicate.** A versioned row is current at release `R` when

```
StartReleaseID <= R  AND  (EndReleaseID IS NULL OR EndReleaseID > R)
```

`EndReleaseID` is **exclusive**: a version is valid on `[Start, End)`. Reading it as inclusive
makes two versions of the same entity valid at the same time, which the model forbids; with the
exclusive reading there are no such overlaps in any versioned table. The predicate is applied in
SQL inside the reader, once per method, so no versioned row reaches the mapping layer unfiltered.
The deliberate exceptions are the dictionary and the `ModuleVersion` history described below,
filtered by `StartReleaseID <= R` only.

**What is cut and what is not.**

| Current at R | Started by R (history up to R) |
|---|---|
| `ModuleVersion`, `TableVersion`, `TableGroup`, `TableGroupComposition`, `SubCategoryVersion` (current version per sub-category) | The dictionary: `ItemCategory`, `SuperCategoryComposition`, `PropertyCategory`, `SubCategoryVersion` (for sub-categories with no current version), `Category` (by `CreatedRelease`), `SubCategory` (through its versions) |

The dictionary is shared across taxonomies and the distribution format keeps renamed codes side
by side (for example both `FGT` and `old-FGT`), so it keeps its history up to R, but nothing that
starts after R. `PropertyCategory` validity windows are still read, because the start release of
each window becomes the release suffix of the dimension code (`eba_dim_4.0:…`). See
[mapping-dpm2.md](mapping-dpm2.md#dictionary).

`ModuleVersion` is also read a second time, as its history up to R
(`StartReleaseID <= R`, without the end condition), to describe the derived taxonomies: the
version, `FromDate` and `ToDate` of a taxonomy depend on the module versions published in earlier
releases (see [mapping-dpm2.md](mapping-dpm2.md#mtaxonomy)). This history only feeds those
columns; no superseded module version is emitted.

**Taxonomies and releases.** Each module version belongs to the taxonomy
`(framework, release in which that module version started)`, and the file of R holds the module
versions that start in R. So from a 4.3 database the 4.2 file holds `corep 4.2` (COREP has not
changed since 4.2) and the 4.3 file holds `tcb 4.3`; there is no `corep 4.3`. This matches the
EBA Annotated Table Layouts, which each release publishes for the modules published in it. See
[mapping-dpm2.md](mapping-dpm2.md#mtaxonomy).

## DPM 1.0 and releases

DPM 1.0 is not versioned by release: one Access file contains every taxonomy published under the
DPM 1.0 model, each labelled with its `DpmPackageCode`, and it is converted into one file. The user chooses what to export with
`--taxonomies`, `--taxonomykeys`, `--releases` or `--all` (see [usage.md](usage.md)), and
`mRelease` is built from the package calendar (see
[mapping-dpm1.md](mapping-dpm1.md#mrelease)).

## Selecting what to convert

For a **DPM 2.0** source the unit is the release: `--release <code>` converts one release, and
without it every release that publishes a module version is converted, one file each.

For a **DPM 1.0** source four mutually exclusive selectors choose the taxonomies; omitting all of
them is an error that lists what is available.

| Flag | Matches | DPM 1.0 example |
|---|---|---|
| `--taxonomies` | `TaxonomyCode` | `COREP 3.2` |
| `--taxonomykeys` | `TaxonomyKey` | `corep/its-005-2020/2022-03-01` or `sbp/4.0` (the segment of `Module.XbrlSchemaRef` between `/fws/` and `/mod/`) |
| `--releases` | DPM package / release code | `3.2` |
| `--all` | every taxonomy | |

`--list-taxonomies --source <file>` prints the detected model followed by the available
taxonomies and their keys; for DPM 2.0 they are grouped by release. See [usage.md](usage.md).
