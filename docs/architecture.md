# Architecture

## Solution structure

```text
EbaDpm.Converter.sln
├─ src/
│  ├─ EbaDpm.Converter.Cli/            Console entry point (assembly name: EbaDpm.Converter)
│  │    CliRunner.cs                   Argument parsing, mode dispatch, console output, exit codes
│  ├─ EbaDpm.Converter.Core/           Shared library: what any pipeline needs, nothing of the 45 tables
│  │    Access/                        ACE provider resolution, source model detection
│  │    Sqlite/                        Batched writing (SqliteBatchWriter)
│  │    Validation/                    Report and check result types; plane C signature and layout reader
│  │    Layouts/                       --extract-layouts: layout workbooks into a SQLite repository
│  │      Xlsx/                        Minimal .xlsx reader (no third-party dependency)
│  ├─ EbaDpm.Converter.Distribution/   The existing pipeline: DPM 1.0 and DPM 2.0 into the 45-table schema
│  │    Access/                        DPM 1.0 reader
│  │      Dpm20/                       DPM 2.0 reader and row types
│  │    Mapping/                       DPM 1.0 transformation into the distribution schema
│  │      Dpm20/                       DPM 2.0 transformation into the distribution schema
│  │    Sqlite/                        Schema creation (SchemaCreator)
│  │    Layouts/                       Loader of the layout dictionary from a 45-table SQLite file
│  │    Validation/                    --validate: planes A, B and C, known exceptions
│  │      Checks/                      Check families
│  │      PlaneC/                      Layout comparison (equality rules, release matching)
│  │    Resources/                     Embedded resources
│  │      dpm-distribution-schema.sql        DDL of the 45 target tables
│  │      plane-c-known-divergences-4.2.tsv  Known layout divergences for release 4.2
│  └─ EbaDpm.Converter.Native/         The native DPM 2.0 pipeline (--target native)
│       Catalog/                       Access catalogue reader, embedded 4.4 baseline, catalogue comparison
│       Schema/                        Exclusions, type mapping, DDL builder, empty baseline schema
│       Conversion/                    1:1 copy of the Access tables into one SQLite file
│       Validation/                    --validate on a native output: plane A, schema drift
│       PlaneC/                        Plane C through the native model; known divergences
│       Layouts/                       Layout dictionary loaded from a native file
│       Resources/                     native-baseline-4.4.json (catalogue of the 4.4 database)
└─ tests/
   ├─ EbaDpm.Converter.Core.Tests/         xUnit tests of the shared project
   ├─ EbaDpm.Converter.Distribution.Tests/ xUnit tests: unit, integration, schema, validation, existing CLI modes
   ├─ EbaDpm.Converter.Native.Tests/       xUnit tests of the native pipeline; project-graph rules
   └─ EbaDpm.Converter.Testing/            shared test support (skip attributes, RepoPaths, fabricated inputs)
```

The boundaries are enforced by project references, not by convention:

```text
Cli ──► Distribution ──► Core
 ├────► Native ────────► Core
 └────► Core
```

`Core` references neither `Distribution` nor `Native`, and `Native` never references
`Distribution`. Embedded resource names follow the project: `EbaDpm.Converter.Distribution.Resources.*`.
`LayoutDictionary` (Core) only holds the codes the layout extractor needs; the loader that fills
it from a 45-table SQLite file (`DistributionLayoutDictionaryLoader`) lives in `Distribution`, and
`LayoutExtractor.Extract` receives a factory for the dictionary.

### Removal path

Removing the existing pipeline is deleting `EbaDpm.Converter.Distribution` and its test project,
plus the CLI modes that use it (conversion to the 45 tables, `--validate`, and the layout
dictionary loader call in `--extract-layouts`). `Core` and `Native` do not change.

External dependencies are limited to `System.Data.OleDb` (reading Access through ACE OLEDB) and
`Microsoft.Data.Sqlite` (writing and reading SQLite) in the library projects, and `Spectre.Console`
and `Spectre.Console.Cli` (argument parsing, tables, progress and colours; both MIT) in the CLI
only: the libraries have no console dependency. All projects target .NET 11 on Windows;
the executable is published for x64 and x86.

## Conversion pipeline

```text
 .accdb ──► SourceModelDetector ──┬─► DPM 1.0 pipeline ──┐
                                  │                       ├─► SQLite (45 tables) ──► VACUUM
                                  └─► DPM 2.0 pipeline ──┘
```

### Source-model detection

`SourceModelDetector` decides which pipeline to run by inspecting the **catalog** of the Access
database (the list of user tables), never its data:

| Catalog | Model |
|---|---|
| A `Domain` table is present | DPM 1.0 |
| A `Category` table is present and `Domain` is absent | DPM 2.0 |
| Neither | Error: the file is not a recognised DPM database. No model is assumed by default |

Requiring both conditions for DPM 2.0 avoids misclassifying older DPM 1.0 databases with a
different layout. If the file cannot even be opened as an Access database, the error names the
file and keeps the provider's original message. Detection is the only branching point between the
two pipelines, and the CLI always prints the detected model.

### DPM 1.0 pipeline

1. Create the empty schema (`SchemaCreator`).
2. Read the source through `Dpm10AccessReader` (behind `IDpmSourceReader`) and resolve the
   requested taxonomies (`TaxonomyKeyResolver`, `TaxonomySelector`).
3. Load, in order: the skeleton — frameworks, releases, taxonomies, owners, base concepts
   (`SkeletonLoader`); the dictionary — domains, members, dimensions, metrics, hierarchies,
   hierarchy nodes, translations (`DictionaryLoader`); templates and tables
   (`TemplateOrTableLoader`); axes, ordinates, categorisations, cells and cell positions
   (`AxisAndCellLoader`); modules (`ModuleLoader`).
4. `VACUUM`.

### DPM 2.0 pipeline

DPM 2.0 is a different metamodel (versioned entities, categories and properties instead of
domains and dimensions), so it has its own reader and loaders rather than sharing an interface
with DPM 1.0.

0. List the releases of the database and how many module versions each one publishes
   (`Dpm20ReleaseCatalog`). The steps below run once per release R to convert: the one given
   with `--release`, or every release that publishes a module version, each into its own file.
1. Open the source with `Dpm20AccessReader` as of R. Every versioned structure table is read as
   of R (`StartRelease <= R AND (EndRelease IS NULL OR EndRelease > R)`); the dictionary and the
   `ModuleVersion` history (which describes the derived taxonomies) are read up to R
   (`StartRelease <= R`).
2. Derive taxonomies from frameworks, modules and releases (`Dpm20TaxonomyDeriver`), since
   DPM 2.0 has no taxonomy entity, and keep those of release R.
3. Create the schema and load, in order: skeleton (`Dpm20SkeletonLoader`), dictionary
   (`Dpm20DictionaryLoader`, with `DimensionMemberResolver`), hierarchy trees
   (`Dpm20HierarchyNodeLoader`), templates and tables (`Dpm20StructureLoader`), axes, ordinates,
   categorisations and cells (`Dpm20AxisAndCellLoader`), modules (`Dpm20ModuleLoader`) and,
   last, concepts and their translations (`Dpm20ConceptLoader`), built from everything written
   before.
4. `VACUUM`.

DPM 2.0 outputs record `Source model = DPM 2.0` in `aDatabaseProperties`, which the validator uses
to select its scope.

### Native DPM 2.0 pipeline

With `--target native` (DPM 2.0 sources only), `EbaDpm.Converter.Native` runs instead of the two
pipelines above, using only the shared components:

1. Detect the source model (`SourceModelDetector`): DPM 1.0 is an argument error. Check the
   baseline gate: the `Release` table must contain release 4.4.
2. Read the Access catalogue (`NativeCatalogReader`): tables, columns, keys, relationships, indexes.
3. Build the DDL from it (`NativeSchemaBuilder`, with `NativeExclusions` and `NativeTypeMapping`)
   and compare the catalogue with the embedded 4.4 baseline (schema drift, informative).
4. Copy every exported table, one at a time, in primary-key order, then create the indexes and
   `VACUUM` (`NativeConverter`). The output is written to a temporary file and moved into place
   only when complete.

Nothing is derived: no release selection, no renumbering, no tie-breaks. See
[target-schema-native.md](target-schema-native.md).

### Writing

- Source tables are read in streaming mode with `OleDbDataReader`; large tables are never loaded
  into a `DataTable`.
- `SqliteBatchWriter` writes with prepared statements in batches inside transactions; bulk-load
  pragmas (no journal, no synchronous writes) are set on the output database for the load.
- The final `VACUUM` makes the file size stable across identical runs; the content is already
  reproducible.

The mapping rules for each model are described in [mapping-dpm1.md](mapping-dpm1.md) and
[mapping-dpm2.md](mapping-dpm2.md); the source models in [source-models.md](source-models.md); the
target in [target-schema.md](target-schema.md).

## Validation and layouts

`Validator` copies the generated database to a temporary file, creates auxiliary indexes there and
runs plane A, then plane B if a reference is given, then plane C if a layout repository is given.
Each check returns a `CheckResult` (plane, layer, statement, examined/failed counts, samples); the
`ValidationReport` is printed to the console and optionally serialised as JSON. Known exceptions
live in `KnownExceptions` and, for plane C, in the embedded TSV file. See
[validation.md](validation.md).

`LayoutExtractor` parses layout file names (`LayoutFileNameParser`), sheet names, header
declarations and cell annotations (`LayoutSheetParser`, `LayoutAnnotationGrammar`), links values to
ordinates (`LayoutValueOrdinateLinker`), parses `==` equality rules from cell comments
(`LayoutEqualityRuleParser`) and writes the repository defined by `LayoutRepositorySchema`. See
[table-layouts.md](table-layouts.md).

## Design principles

**Sources of truth.** The Access database and the EBA Annotated Table Layouts are the sources of
truth. A reference SQLite export produced by a third party is used as an instrument for detecting
regressions, never as the criterion that settles a disagreement. When the reference and the Access
database disagree, the output follows the Access database.

**No invented data.** The converter emits what the source declares. Tables without a source are
created empty (for example the validation-rule tables), optional references without a value are
`NULL`, and objects that exist in another export but not in the Access database are not
reproduced. The few literal rows the converter writes (for example `aDatabaseProperties` and the
URI rewrite rows of a DPM 2.0 output) are fixed metadata of the generator, not data.

**Exact target schema.** The output is created from the embedded DDL and must be structurally
identical to the distribution schema: table and column names are kept literally.

**Comparison by business key.** Surrogate IDs are not stable between independent exports, so
every comparison — in tests and in `--validate` — uses business keys: codes, XBRL codes, ordinate
paths and signatures.

**Nothing silent.** Unresolved cases are listed by name in the conversion output; unparsed layout
content is stored with a reason; validation always reports coverage alongside results, and a check
that examined nothing is reported as skipped rather than passed.

**Choosing the ACE OLEDB provider.** ACE OLEDB is the only supported way to read `.accdb`
files, which makes the tool Windows-only. It comes with Microsoft Office or with the Access
Database Engine 2016 Redistributable, which register the same providers. `AceProvider` takes,
among the providers registered for the architecture of the running process, the first of
`Microsoft.ACE.OLEDB.16.0` and `Microsoft.ACE.OLEDB.12.0` that actually opens the file, and
reports every candidate it tried when none does. An OLEDB provider can only be loaded by a
process of its own architecture, so the release ships a 64-bit and a 32-bit executable. When
the 64-bit process finds no 64-bit provider but a 32-bit one is registered, it relaunches
`x86\EbaDpm.Converter.exe` with the same arguments and returns its exit code
(`ArchitectureRelauncher`; the `EBADPM_RELAUNCHED` variable prevents a loop).

**Single-process access to ACE OLEDB.** The provider is not
safe for concurrent use from several threads of one process — it fails with access violations
(`0xC0000005`) or provider errors even when each thread opens a different file or connection
pooling is disabled — because its state is global to the process. The converter therefore reads
Access from a single thread, and the test suite runs its test collections sequentially. To convert
several databases in parallel, start several processes.
