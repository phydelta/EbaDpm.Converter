# Command-line reference

`EbaDpm.Converter` has five modes. The mode is chosen by the flag present on the command line;
anything that is not one of the other modes is a conversion.

| Mode | Purpose |
|---|---|
| [Convert](#convert) | Convert an Access DPM database into a SQLite database in the distribution schema, or ([`--target native`](#native-output)) into the native DPM 2.0 output |
| [`--list-taxonomies`](#list-taxonomies) | List the taxonomies an Access database contains, with their selectors |
| [`--schema-only`](#schema-only) | Create an empty database with the 45 tables of the distribution schema (or the native baseline schema with `--target native`) |
| [`--validate`](#validate) | Validate a generated database (planes A, B and C; a native output has its own rule set) |
| [`--extract-layouts`](#extract-layouts) | Build a SQLite repository from the EBA Annotated Table Layout workbooks |
| `--help`, `-h`, `-?`, `/?` | Print the help and exit with code 0 |

When running from source, replace `EbaDpm.Converter` with
`dotnet run --project src/EbaDpm.Converter.Cli --`.

## General rules

- Option names are case-insensitive.
- An option that takes a value must be followed by it; a value cannot start with `--`.
- Values containing spaces must be quoted.
- List values (`--taxonomies`, `--taxonomykeys`, `--releases`) are comma-separated; surrounding
  spaces are trimmed, and values are matched case-insensitively.
- Any argument not recognised by the selected mode is an error (exit code 1).
- `--plain` is accepted with every mode and forces [plain output](#console-output).
- The help flags win over everything else and need no other argument.
- Mode precedence, if several mode flags are given: help, `--schema-only`, `--list-taxonomies`,
  `--validate`, `--extract-layouts`, then conversion.

## Console output

The command line is parsed with Spectre.Console.Cli (a single default command; the mode is the
flag present, so there are no sub-commands). `--help` prints the generated help: every option with
its description and one example per mode.

- **On a terminal**: conversions and validations show live progress by phase (read the source,
  one phase per area loaded, `VACUUM`, `Validate`) with the elapsed time of each; summaries are
  tables; the planes of `--validate` are coloured (green: pass, blue: known exception that applies,
  yellow: informative difference, red: critical difference or stale exception, the causes of exit
  code 3); errors are shown as `Error: ...` with the label in red.
- **Redirected** (a pipe, a file, CI, the 32-bit relaunch of the tool) **or with `--plain`**: plain
  text. No ANSI escape sequences, no live widgets, tables without borders, and one line per
  finished phase (for example `[  5.0 s] Dictionary`) so a log shows where a long run is. The
  stream is detected independently: with `> out.txt` the errors on the terminal stay coloured.
- **`NO_COLOR`**: when the environment variable is set and not empty, colours are off (terminal
  features stay on). Use `--plain` to turn everything off.
- **Unexpected failures** (not an argument, input or output error) are shown as
  `Error: unexpected failure (<type>): <message>` followed by the exception details, and end with
  exit code 1.

The shape of the text on standard output (tables, wording, phase lines) is meant for people and
may change between versions; do not parse it. The JSON written by `--report` is the machine
interface and its structure is stable.

## Convert

The options depend on the source model, which is detected automatically and printed on the first
line of output.

### DPM 2.0 sources

```text
EbaDpm.Converter --source "<input.accdb>" --output <path>
                 [--release <code>] [--overwrite] [--report <path>] [--verbose]
```

| Option | Required | Description |
|---|---|---|
| `--source <path>` | yes | The Access DPM database (`.accdb`). Opened read-only |
| `--output <path>` | yes | With `--release`: the SQLite file to create. Without it: the directory that receives one file per release, created if needed |
| `--release <code>` | no | Convert only the release with that `Release.Code` (e.g. `4.3`). An unknown code is an error that lists the available codes. A release in which no module version is published is an error and writes nothing |
| `--overwrite` | no | Replace output files that exist. Without it, an existing output file is an error, and nothing is written |
| `--report <path>` | no | Accepted for compatibility; the conversion summary is currently written to standard output only |
| `--verbose` | no | Accepted for compatibility; currently has no additional effect |

> **What a DPM 2.0 export contains.** Each SQLite file is one EBA release: the dictionary,
> hierarchies and table layouts as of that release, and only the module versions first published
> in it. A module that did not change in a release stays in the file of the release that published
> it (in the 4.4 database, COREP is in the 4.2 file). Without `--release`, one file is written per
> release; releases with no new module versions are skipped. Later Access databases correct
> earlier data, so the file of a release can differ slightly depending on the database it is
> produced from.

Without `--release`, the files are named `dpm-<release>.db` (for example `dpm-4.2.1.db`) and are
written in release order. Every target file is checked before the first one is written. A
consumer that maps returns to cells loads the files of every release it needs, and picks for each
module the file of the release in its `XBRLSchemaRef` (`.../fws/<framework>/<release>/mod/...`).

The DPM 1.0 selectors (`--all`, `--taxonomies`, `--taxonomykeys`, `--releases`) are errors with a
DPM 2.0 source: a DPM 2.0 taxonomy code already carries its release (`corep 4.2`), and the release
is chosen with `--release`. The `--cutoff-release` option of earlier versions no longer exists.

### DPM 1.0 sources

```text
EbaDpm.Converter --source "<input.accdb>" --output "<output.db>"
                 (--taxonomies "..." | --taxonomykeys "..." | --releases "..." | --all)
                 [--overwrite] [--report <file.json>] [--verbose]
```

| Option | Required | Description |
|---|---|---|
| `--source <path>` | yes | The Access DPM database (`.accdb`). Opened read-only |
| `--output <path>` | yes | The SQLite database to create. Parent directories are created if needed |
| `--all` | one selector | Convert every taxonomy in the source |
| `--taxonomies <list>` | one selector | Select by `TaxonomyCode`, as printed by `--list-taxonomies` (e.g. `COREP 3.2`) |
| `--taxonomykeys <list>` | one selector | Select by `TaxonomyKey`, a derived identifier (e.g. `corep/its-005-2020/2022-03-01` up to 3.x, `sbp/4.0` from 4.0) |
| `--releases <list>` | one selector | Select by DPM release (`DpmPackageCode`, e.g. `4.0,4.1`) |
| `--overwrite`, `--report`, `--verbose` | no | As for DPM 2.0 sources |

**Exactly one selector is mandatory.** With none, the command fails and prints the available
taxonomies; with more than one, it fails as well. Unknown codes, keys or releases are reported by
name together with the valid values. `--release` is an error with a DPM 1.0 source.

### Output

While it runs, each conversion reports its phases with their elapsed time (see
[Console output](#console-output)). It ends with a summary table (rows written per area, plus any
unresolved cases listed by name below it) and the time of the final `VACUUM`. For DPM 2.0 the release of each file is printed
before its summary, and each skipped release is named.

Examples:

```powershell
# DPM 2.0: one file per release, in the directory "eba"
EbaDpm.Converter --source "DPM2 Database_v 4_4.accdb" --output eba

# DPM 2.0: the 4.2 release only, replacing a previous output
EbaDpm.Converter --source "DPM2 Database_v 4_4.accdb" --output eba-4.2.db --release 4.2 --overwrite

# DPM 1.0: by release
EbaDpm.Converter --source "DPM 1.0 Database_v4_1.accdb" --output dpm1.db --releases 4.0,4.1
```

## Native output

```text
EbaDpm.Converter --source "<dpm2.accdb>" --output <file.db> --target native
                 [--overwrite] [--report <path>] [--verbose] [--plain]
EbaDpm.Converter --schema-only --output <file.db> --target native [--overwrite]
```

`--target <distribution|native>` chooses the output schema of a conversion and of `--schema-only`.
`distribution` is the default and is the 45-table schema described in the rest of this page; every
command line without `--target` behaves as before. Values are lower case; anything else is an error
that lists the valid ones. `--target` with any other mode (`--list-taxonomies`, `--validate`,
`--extract-layouts`) is an error, like any other option that belongs to another mode.

`--target native` writes an image of the DPM 2.0 Access database in one SQLite file: the same table
and column names, rows, IDs and values, every release in the file, nothing derived. What it
contains, the type mapping and the keys are in [target-schema-native.md](target-schema-native.md).

| Option | Description |
|---|---|
| `--source <path>` | Required. A DPM 2.0 Access database that contains release 4.4 (the baseline) |
| `--output <path>` | Required. The SQLite file to create (a directory is an error). The file is built beside it and moved into place when complete, so a failed conversion leaves no partial file |
| `--overwrite` | Replace the output file if it exists. Without it an existing file is an error and nothing is written |
| `--report`, `--verbose`, `--plain` | As for the other conversions |

With `--schema-only`, the empty baseline (4.4) schema, with its indexes, is created; no Access
database is needed.

Errors (exit code 1, nothing written):

- `--release`: the native output holds every release of the source in one file.
- `--taxonomies`, `--taxonomykeys`, `--releases`, `--all`: they do not apply, the whole database is copied.
- A DPM 1.0 source: the native output exists only for DPM 2.0 sources.
- A source whose `Release` table has no row with code `4.4` (the 4.2.1 and 4.3 publications): the
  native output starts from the 4.4 schema. The message names the current release of the source.
- An Access column type that is not in the type mapping: the message names the column.

The conversion reports its stages and each table copied (rows and time), then a summary: tables and
rows written, the excluded tables found in the source, the foreign keys declared and not declared,
the indexes, the schema differences against the baseline (a later publication that changes the
schema converts with its own schema; the differences are listed and never fail the conversion), and
the time and size of the file.

```powershell
EbaDpm.Converter --source "DPM2 Database_v 4_4.accdb" --output dpm2-native.db --target native
EbaDpm.Converter --schema-only --output native-empty.db --target native
```

Measured on the 4.4 database (2026-11-25, x64): about 45 seconds and a 599 MB file (59 tables, 5,568,105 rows). Figures of one publication and one machine, not of the format.

## List taxonomies

```text
EbaDpm.Converter --list-taxonomies --source "<input.accdb>" [--release <code>]
```

Prints the detected source model and one row per taxonomy with its `TaxonomyKey`, `TaxonomyCode`,
DPM package (release), publication date and number of tables. For DPM 1.0 sources, use it to find
the values to pass to `--taxonomies`, `--taxonomykeys` or `--releases`.

For DPM 2.0 sources the taxonomies are derived (framework plus release), so the publication date
column shows `-`. Without `--release` the taxonomies are grouped by release, in release order, and
the releases that a conversion would skip are marked; with `--release` only that release is
listed. `--release` is an error with a DPM 1.0 source.

## Schema only

```text
EbaDpm.Converter --schema-only --output "<output.db>" [--overwrite]
```

Creates the 45 tables of the distribution schema, empty, from
[`dpm-distribution-schema.sql`](../src/EbaDpm.Converter.Distribution/Resources/dpm-distribution-schema.sql).
No Access database is needed. With `--target native` it creates the empty [native schema](#native-output)
(baseline 4.4) instead.

## Validate

```text
EbaDpm.Converter --validate "<generated.db>" [--reference "<reference.db>"]
                 [--layouts "<layouts.db>"] [--report <file.json>]
```

| Option | Required | Description |
|---|---|---|
| `--validate <path>` | yes | The generated SQLite database to validate. It is never modified: validation works on a temporary copy |
| `--reference <path>` | no | A reference SQLite export of the DPM in the distribution schema. Enables plane B |
| `--layouts <path>` | no | A layout repository produced by `--extract-layouts`. Enables plane C |
| `--report <path>` | no | Write the full validation report as JSON |

A **native output** (`--target native`) is detected and validated with its own rule set; see
[Validate a native output](#validate-a-native-output) below.

- **Plane A** (internal invariants) always runs.
- **Plane B** runs only with `--reference`. It is informational: its failures are reported and
  counted separately, but never change the exit code.
- **Plane C** runs only with `--layouts`. When it does not run, the output says so explicitly.

The reference role is inferred from the reference file name: a name containing `3.2`, `4.0` or
`4.2` selects the known-exception set and comparison scope recorded for that release. The schema
comparison (`B-SCH-01`) runs only against a 4.2 reference. Only taxonomies present in both
databases are compared, always by business key (codes, XBRL codes, signatures), never by
surrogate ID.

Console output shows, per plane, a table with the number of checks per layer (critical /
informative), how many passed and how many have differences, followed by the failing checks with
up to ten sample business keys each. It then summarises the known exceptions (applied, stale, not
evaluated) in a table and prints a final `RESULT` line. The JSON report
contains every check with its statement, counts, timing and up to 50 samples, plus the complete
list of known exceptions and their outcome.

Examples:

```powershell
# Internal invariants only
EbaDpm.Converter --validate eba-4.2.db

# Add the comparison against the EBA Annotated Table Layouts
EbaDpm.Converter --validate eba-4.2.db --layouts layouts-4.2.db

# Add an informational comparison against a reference export, and save the JSON report
EbaDpm.Converter --validate eba-4.2.db --reference EBA_4.2_Hotfix.db --layouts layouts-4.2.db `
  --report reports/validation.json
```

See [validation.md](validation.md) for what each plane checks.

### Validate a native output

```text
EbaDpm.Converter --validate "<native.db>" [--source "<dpm2.accdb>"]
                 [--layouts "<layouts.db>"] [--report <file.json>]
```

`--validate` recognises a native DPM 2.0 output (the tables `Category` and `TableVersionCell` and no
`mModule`) and runs its own plane A ([validation.md](validation.md#native-output)).

| Option | Description |
|---|---|
| `--source <path>` | The Access database the file was converted from. Valid with `--validate` only for a native output (with a 45-table output it is an error). It enables completeness, row counts, content against the source and the schema drift report; without it those checks are reported as skipped, never as passed |
| `--reference <path>` | Not applicable: plane B has no reference export for the native output (argument error) |
| `--layouts`, `--report` | As above (`--report` also carries the schema drift) |

```powershell
EbaDpm.Converter --validate dpm2-native.db --source "DPM2 Database_v 4_4.accdb" --report reports/native.json
```

Measured on the 4.4 database (2026-11-25, x64): about 36 seconds against the source, with the whole
content (5,568,105 rows) compared. Figures of one publication and one machine, not of the format.

## Extract layouts

```text
EbaDpm.Converter --extract-layouts "<directory>" --out "<layouts.db>"
                 --dictionary "<converted.db>" [--overwrite]
```

| Option | Required | Description |
|---|---|---|
| `--extract-layouts <dir>` | yes | Directory containing the Annotated Table Layout workbooks (`*.xlsx`), searched recursively |
| `--out <path>` | yes | The layout repository (SQLite) to create |
| `--dictionary <path>` | yes | A database already converted by this tool (distribution schema), used to classify annotations and, for the 4.4 file-name format, to resolve the framework of each module. It must be the file of the layouts' release |
| `--overwrite` | no | Replace the repository if it exists |

`--dictionary` is mandatory. See [table-layouts.md](table-layouts.md) for the file-name formats
recognised, why the dictionary is needed and the structure of the repository.

The 4.4 layouts are named `<date> <MODULE>_table layout <REL>.xlsx` (for example
`20261007 AML_ELIGIBILITY_table layout 4.4.xlsx`) and do not carry the framework. It is looked up
from the module code in the dictionary, so `--dictionary` must be the file of the layouts' release
(`dpm-4.4.db` for the 4.4 layouts: a release publishes layouts only for the module versions that
start in it, and those are the modules of its file). A module code that is not in the dictionary
is an error that names the file, the code and the dictionary; a framework is never guessed.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success. For `--validate`: no critical failure in planes A or C, and no stale known exception in those planes (plane B differences and informative differences do not count) |
| `1` | Missing or invalid arguments, an input/output error, or an unexpected failure: unknown option, missing value, file not found, output already exists without `--overwrite`, source that is not a recognisable DPM Access database, unknown taxonomy or release, a DPM 2.0 release with no module version published in it |
| `2` | Reserved for a recognised but unimplemented mode. Not returned by the current version |
| `3` | `--validate` only: at least one critical check failed in plane A or C, or a known exception in those planes became stale |
