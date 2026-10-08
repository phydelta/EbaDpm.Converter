# Source models

The converter reads the EBA **DPM Database** published as a Microsoft Access file (`.accdb`).
The EBA has published that database in two structurally different data models, and the
converter supports both:

| Model | Typical files | Releases covered | Pipeline |
|---|---|---|---|
| **DPM 1.0** | `DPM 1.0 Database_v4_1_20250709.accdb` | Up to release **4.1** (the last DPM 1.0 publication) | `Access/Dpm10AccessReader` + `Mapping/*Loader` |
| **DPM 2.0** | `DPM2 Database_v 4_2_20251125.accdb`, `DPM2 Database_v 4_3_20260622.accdb` | Release **4.2** onwards | `Access/Dpm20/Dpm20AccessReader` + `Mapping/Dpm20/*` |

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
| Not used | The DPM-ML validation operations (`Operation*`) — validation rules are out of scope |

Differences from DPM 1.0 that matter to the converter (details in
[mapping-dpm2.md](mapping-dpm2.md)):

- **There is no `Taxonomy` table.** Taxonomies are derived from frameworks, releases and the
  validity windows of table versions.
- **There is no `Concept` table.** All concepts are minted by the converter.
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
| `DPM2 Database_v 4_3_20260622.accdb` | the above plus 4.2.1, 4.2.1.1, 4.2.1.2, 4.3 | 4.3 |

The `ReleaseID` numbering is not stable between publications (release 4.3 uses IDs in the
`1010000xxx` range while 4.2 used 1–5), so the converter never relies on ID ranges or positions —
only on the maximum ID and on `Release.Code`.

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
misclassified by a single-table test.

If the file cannot be opened as an Access database at all (wrong path, a `.db` file, a
truncated file), the provider error is re-raised with the file path and the expected formats,
keeping the original provider message as the inner exception.

The detected model is always written to the console output, and DPM 2.0 outputs additionally
record it in `aDatabaseProperties` (`'Source model' = 'DPM 2.0'`). The validator uses that row
to decide which rule set applies; DPM 1.0 outputs leave the row absent so their content is
unchanged.

## The cutoff release (DPM 2.0)

A DPM 2.0 database contains several releases at once, so every read of a versioned table is
made **as of a single release**, the *cutoff release*.

**Derivation.** `Dpm20AccessReader.Open()` reads `[Release]` and takes the row with the highest
`ReleaseID` (the reader also accepts an explicit `ReleaseID` through its constructor, used by
tests). The CLI always logs the release it chose. In both published files the highest ID is also
the most recent release.

**Validity predicate.** A versioned row is current at release `R` when

```
StartReleaseID <= R  AND  (EndReleaseID IS NULL OR EndReleaseID > R)
```

`EndReleaseID` is **exclusive**: a version is valid on `[Start, End)`. Reading it as inclusive
makes two versions of the same entity valid at the same time, which the model forbids; with the
exclusive reading there are no such overlaps in any versioned table. The predicate is applied in
SQL inside the reader, once per method, so no versioned row reaches the mapping layer unfiltered.

**What is cut and what is not.**

| Filtered at the cutoff | Not filtered (full history) |
|---|---|
| `ModuleVersion`, `TableVersion`, `TableGroup`, `TableGroupComposition`, `SubCategoryVersion` (current version per sub-category) | The dictionary: `Category`, `ItemCategory`, `SuperCategoryComposition`, `SubCategory`, `PropertyCategory` |

The dictionary is shared across taxonomies and the distribution format keeps renamed codes side
by side (for example both `FGT` and `old-FGT`), so it is exported in full. `PropertyCategory`
validity windows are still read, because the start release of each window becomes the release
suffix of the dimension code (`eba_dim_4.0:…`).

**Taxonomies and the cutoff.** Each emitted table version produces up to two derived taxonomies:
`(framework, cutoff release)` and `(framework, release in which that table version started)`.
So a 4.3 database yields `corep 4.3` together with older taxonomies such as `corep 4.0` for
tables unchanged since 4.0. The template tree, modules and module templates exist only for the
cutoff-release taxonomies. See [mapping-dpm2.md](mapping-dpm2.md#mtaxonomy).

## DPM 1.0 and releases

DPM 1.0 has no cutoff: one Access file contains every taxonomy published under the DPM 1.0
model, each labelled with its `DpmPackageCode`. The user chooses what to export with
`--taxonomies`, `--taxonomykeys`, `--releases` or `--all` (see [usage.md](usage.md)), and
`mRelease` is built from the package calendar (see
[mapping-dpm1.md](mapping-dpm1.md#mrelease)).

## Selecting taxonomies

The same four mutually exclusive selectors work for both models; omitting all of them is an
error that lists what is available.

| Flag | Matches | DPM 1.0 example | DPM 2.0 example |
|---|---|---|---|
| `--taxonomies` | `TaxonomyCode` | `COREP 3.2` | `corep 4.2` |
| `--taxonomykeys` | `TaxonomyKey` | `corep/its-005-2020/2022-03-01` or `sbp/4.0` (the segment of `Module.XbrlSchemaRef` between `/fws/` and `/mod/`) | `corep/4.2` (framework in lower case + `/` + release) |
| `--releases` | DPM package / release code | `3.2` | `4.2` |
| `--all` | every taxonomy | | |

`--list-taxonomies --source <file>` prints the detected model (and, for DPM 2.0, the cutoff
release) followed by the available taxonomies and their keys.
