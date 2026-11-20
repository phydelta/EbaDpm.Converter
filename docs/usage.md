# Command-line reference

`EbaDpm.Converter` has five modes. The mode is chosen by the flag present on the command line;
anything that is not one of the other modes is a conversion.

| Mode | Purpose |
|---|---|
| [Convert](#convert) | Convert an Access DPM database into a SQLite database in the distribution schema |
| [`--list-taxonomies`](#list-taxonomies) | List the taxonomies an Access database contains, with their selectors |
| [`--schema-only`](#schema-only) | Create an empty database with the 45 tables of the distribution schema |
| [`--validate`](#validate) | Validate a generated database (planes A, B and C) |
| [`--extract-layouts`](#extract-layouts) | Build a SQLite repository from the EBA Annotated Table Layout workbooks |
| `--help`, `-h`, `-?`, `/?` | Print usage and exit with code 0 |

When running from source, replace `EbaDpm.Converter` with
`dotnet run --project src/EbaDpm.Converter.Cli --`.

## General rules

- Option names are case-insensitive.
- An option that takes a value must be followed by it; a value cannot start with `--`.
- Values containing spaces must be quoted.
- List values (`--taxonomies`, `--taxonomykeys`, `--releases`) are comma-separated; surrounding
  spaces are trimmed, and values are matched case-insensitively.
- Any argument not recognised by the selected mode is an error (exit code 1).
- The help flags win over everything else and need no other argument.
- Mode precedence, if several mode flags are given: help, `--schema-only`, `--list-taxonomies`,
  `--validate`, `--extract-layouts`, then conversion.

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

Each conversion ends with a per-area summary (rows written per table group, plus any unresolved
cases listed by name) and a final `VACUUM`. For DPM 2.0 the release of each file is printed
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
[`dpm-distribution-schema.sql`](../src/EbaDpm.Converter.Core/Resources/dpm-distribution-schema.sql).
No Access database is needed.

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

- **Plane A** (internal invariants) always runs.
- **Plane B** runs only with `--reference`. It is informational: its failures are reported and
  counted separately, but never change the exit code.
- **Plane C** runs only with `--layouts`. When it does not run, the output says so explicitly.

The reference role is inferred from the reference file name: a name containing `3.2`, `4.0` or
`4.2` selects the known-exception set and comparison scope recorded for that release. The schema
comparison (`B-SCH-01`) runs only against a 4.2 reference. Only taxonomies present in both
databases are compared, always by business key (codes, XBRL codes, signatures), never by
surrogate ID.

Console output lists, per plane and layer (critical / informative), the number of checks, how many
passed and the failing ones with up to ten sample business keys each. It then summarises the
known exceptions (applied, stale, not evaluated) and prints a final `RESULT` line. The JSON report
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
| `1` | Missing or invalid arguments, or an input/output error: unknown option, missing value, file not found, output already exists without `--overwrite`, source that is not a recognisable DPM Access database, unknown taxonomy or release, a DPM 2.0 release with no module version published in it |
| `2` | Reserved for a recognised but unimplemented mode. Not returned by the current version |
| `3` | `--validate` only: at least one critical check failed in plane A or C, or a known exception in those planes became stale |
