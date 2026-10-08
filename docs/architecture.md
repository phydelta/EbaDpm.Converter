# Architecture

## Solution structure

```text
EbaDpm.Converter.sln
├─ src/
│  ├─ EbaDpm.Converter.Cli/        Console entry point (assembly name: EbaDpm.Converter)
│  │    CliRunner.cs               Argument parsing, mode dispatch, console output, exit codes
│  └─ EbaDpm.Converter.Core/       Class library with all the logic
│       Access/                    Reading the .accdb through ACE OLEDB
│         Dpm20/                   DPM 2.0 reader and row types
│       Mapping/                   DPM 1.0 transformation into the distribution schema
│         Dpm20/                   DPM 2.0 transformation into the distribution schema
│       Sqlite/                    Schema creation and batched writing
│       Validation/                --validate: planes A, B and C, known exceptions, report
│         Checks/                  Check families
│         PlaneC/                  Layout comparison (signatures, equality rules, release matching)
│       Layouts/                   --extract-layouts: layout workbooks into a SQLite repository
│         Xlsx/                    Minimal .xlsx reader (no third-party dependency)
│       Resources/                 Embedded resources
│         dpm-distribution-schema.sql        DDL of the 45 target tables
│         plane-c-known-divergences-4.2.tsv  Known layout divergences for release 4.2
└─ tests/
   └─ EbaDpm.Converter.Tests/      xUnit tests: unit, integration, schema, validation
```

External dependencies are limited to `System.Data.OleDb` (reading Access through
`Microsoft.ACE.OLEDB.16.0`) and `Microsoft.Data.Sqlite` (writing and reading SQLite). Both
projects target .NET 10, x64, Windows.

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

1. Open the source with `Dpm20AccessReader` and resolve the **cutoff release**: the latest release
   in the database. Every versioned table is read as of that release
   (`StartRelease <= R AND (EndRelease IS NULL OR EndRelease > R)`).
2. Derive taxonomies from frameworks, modules and releases (`Dpm20TaxonomyDeriver`), since
   DPM 2.0 has no taxonomy entity, and apply the selector.
3. Create the schema and load, in order: skeleton (`Dpm20SkeletonLoader`), dictionary
   (`Dpm20DictionaryLoader`, with `DimensionMemberResolver`), hierarchy trees
   (`Dpm20HierarchyNodeLoader`), templates and tables (`Dpm20StructureLoader`), axes, ordinates,
   categorisations and cells (`Dpm20AxisAndCellLoader`), modules (`Dpm20ModuleLoader`) and,
   last, concepts and their translations (`Dpm20ConceptLoader`), built from everything written
   before.
4. `VACUUM`.

DPM 2.0 outputs record `Source model = DPM 2.0` in `aDatabaseProperties`, which the validator uses
to select its scope.

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

**Windows-only, single-process access to ACE OLEDB.** `Microsoft.ACE.OLEDB.16.0` is the only
supported way to read `.accdb` files, which makes the tool Windows x64 only. The provider is not
safe for concurrent use from several threads of one process — it fails with access violations
(`0xC0000005`) or provider errors even when each thread opens a different file or connection
pooling is disabled — because its state is global to the process. The converter therefore reads
Access from a single thread, and the test suite runs its test collections sequentially. To convert
several databases in parallel, start several processes.
