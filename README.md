# EbaDpm.Converter

[![CI](https://github.com/phydelta/EbaDpm.Converter/actions/workflows/ci.yml/badge.svg)](https://github.com/phydelta/EbaDpm.Converter/actions/workflows/ci.yml)
[![CodeQL](https://github.com/phydelta/EbaDpm.Converter/actions/workflows/codeql.yml/badge.svg)](https://github.com/phydelta/EbaDpm.Converter/actions/workflows/codeql.yml)
[![Release](https://img.shields.io/github/v/release/phydelta/EbaDpm.Converter)](https://github.com/phydelta/EbaDpm.Converter/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A command-line tool that converts the **EBA DPM Database** (Data Point Model) from the Microsoft
Access edition published by the European Banking Authority (`.accdb`) into **SQLite**, using the
45-table **DPM distribution schema** (`mXxx` / `dXxx` / `aXxx` / `vXxx` tables) consumed by
XBRL, reporting and validation tooling.

It is not a table-by-table dump. The Access model and the distribution schema are structurally
different, so the converter performs a **data-model transformation**: it derives table axes,
ordinates, cells, cell positions, dimension-member signatures, modules and concepts from the
source metamodel.

| | |
|---|---|
| **Version** | 1.1.0 |
| **Sources** | DPM 1.0 (e.g. release 4.1) and DPM 2.0 (e.g. releases 4.2, 4.3), detected automatically |
| **Output** | SQLite database with the 45 tables of the DPM distribution schema |
| **Platform** | Windows x64 |
| **License** | MIT |

## Why

The EBA publishes the DPM as a Microsoft Access database. A SQLite edition in the distribution
schema is what most downstream tools consume, but such an edition often becomes available only
some time after the Access publication. This tool produces it **on publication day**, directly
from the Access database, and ships with a validation suite that checks the result against
internal invariants and against the EBA's own Annotated Table Layouts — neither of which depends
on any other SQLite edition existing.

## Features

- **Two source models, one command.** DPM 1.0 and DPM 2.0 Access databases are detected from the
  catalog (never from data) and routed to separate conversion pipelines that write the same
  target schema.
- **Taxonomy selection.** Convert everything, or select by taxonomy code, taxonomy key or DPM
  release.
- **Exact target schema.** The output is created from an embedded DDL
  ([`dpm-distribution-schema.sql`](src/EbaDpm.Converter.Core/Resources/dpm-distribution-schema.sql))
  and is structurally identical to the distribution schema.
- **Reproducible output.** The same input and parameters produce the same content; the file is
  vacuumed at the end so its size is stable too.
- **Built-in validation** (`--validate`) in three independent planes: internal invariants, an
  optional comparison against a reference SQLite export, and an optional signature-by-signature
  comparison against the EBA Annotated Table Layouts.
- **Layout extraction** (`--extract-layouts`): turns the EBA Annotated Table Layout workbooks
  (`.xlsx`) into a queryable SQLite repository, usable on its own.
- **Single-file executable.** Can be published as one self-contained `.exe` with no .NET
  runtime prerequisite.

## Requirements

| Requirement | Notes |
|---|---|
| Windows x64 | The tool is Windows-only because of the Access provider below |
| Microsoft Access Database Engine 2016 Redistributable (x64) | Provides `Microsoft.ACE.OLEDB.16.0`, used to read the `.accdb`. It is a system component and cannot be bundled |
| .NET 10 SDK | Only to build from source. The self-contained executable needs no .NET installation |

## Download

Ready-to-run executables are published on the
[Releases](https://github.com/phydelta/EbaDpm.Converter/releases) page as
`EbaDpm.Converter-X.Y.Z-win-x64.zip`, with a `SHA256SUMS.txt` file and a build provenance
attestation (see [docs/releasing.md](docs/releasing.md#verifying-a-downloaded-release)).

## Build

```powershell
dotnet build EbaDpm.Converter.sln -c Release
```

Run from source:

```powershell
dotnet run --project src/EbaDpm.Converter.Cli -- <arguments>
```

Publish a single-file, self-contained executable:

```powershell
dotnet publish src/EbaDpm.Converter.Cli -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

This produces `dist\EbaDpm.Converter.exe` (one file, roughly 75 MB). It still requires
`Microsoft.ACE.OLEDB.16.0` on the machine to read Access files.

## Quick start

```powershell
# 1. See which taxonomies the Access database contains
EbaDpm.Converter --list-taxonomies --source "DPM2 Database_v 4_2.accdb"

# 2. Convert the whole database (the source model is detected automatically)
EbaDpm.Converter --source "DPM2 Database_v 4_2.accdb" --output eba-4.2.db --all

#    ...or only some taxonomies / releases
EbaDpm.Converter --source "DPM2 Database_v 4_2.accdb" --output corep.db --taxonomies "COREP 4.2,IF 4.2"
EbaDpm.Converter --source "DPM 1.0 Database_v4_1.accdb" --output out.db --releases 4.0,4.1

# 3. Validate the output (internal invariants; no external input needed)
EbaDpm.Converter --validate eba-4.2.db

# 4. Optionally build a layout repository from the EBA Annotated Table Layouts
#    and validate against it as well
EbaDpm.Converter --extract-layouts "4.2 table layouts" --out layouts-4.2.db --dictionary eba-4.2.db
EbaDpm.Converter --validate eba-4.2.db --layouts layouts-4.2.db --report validation.json
```

Exactly one taxonomy selector is required for a conversion: `--all`, `--taxonomies`,
`--taxonomykeys` or `--releases`. See [docs/usage.md](docs/usage.md) for every option and exit code.

## Validation overview

| Plane | Compares the output against | Can fail the run |
|---|---|---|
| **A** | Itself — internal invariants of the distribution schema. Always runs | Yes |
| **B** | A reference SQLite export of the DPM produced by a third party (`--reference`) | No — informational only |
| **C** | The EBA Annotated Table Layouts, via a layout repository (`--layouts`) | Yes |

The order of authority is **Access database → Annotated Table Layouts → reference export**. A
reference export is another implementation of the same conversion, not a specification, so a
difference against it is reported but never treated as a defect on its own. Details in
[docs/validation.md](docs/validation.md).

## Known limitations

- **DPM validation rules are not exported.** `vValidationRuleExpressions` and
  `vValidationRuleTables` are created empty. This is a deliberate scope decision.
- **Windows-only**, because of the `Microsoft.ACE.OLEDB.16.0` dependency.
- **No in-process concurrency over Access.** The ACE OLEDB provider is not safe for concurrent
  use from several threads of the same process, even on different files (it can crash with
  `0xC0000005`). Run conversions in parallel as separate processes.
- **Plane C cannot cover every DPM 1.0 taxonomy.** The EBA does not publish Annotated Table
  Layouts for the 2.x releases (2014–2017), so for most historical DPM 1.0 taxonomies there is no
  independent layout to compare against.
- **Plane C ignores default members.** Datapoint signatures omit default members, so the layout
  comparison says nothing about those categorisation rows.
- A few objects present in the reference export are not declared by the Access database (three
  `qTR` members) and are therefore not emitted.

## Documentation

| Document | Contents |
|---|---|
| [docs/usage.md](docs/usage.md) | Full command-line reference, examples and exit codes |
| [docs/validation.md](docs/validation.md) | The three validation planes, check families, known exceptions, fidelity |
| [docs/table-layouts.md](docs/table-layouts.md) | Extracting the EBA Annotated Table Layouts into SQLite |
| [docs/architecture.md](docs/architecture.md) | Solution structure, pipeline and design principles |
| [docs/source-models.md](docs/source-models.md) | The DPM 1.0 and DPM 2.0 Access source models |
| [docs/mapping-dpm1.md](docs/mapping-dpm1.md) | Mapping rules from DPM 1.0 to the distribution schema |
| [docs/mapping-dpm2.md](docs/mapping-dpm2.md) | Mapping rules from DPM 2.0 to the distribution schema |
| [docs/target-schema.md](docs/target-schema.md) | The 45-table DPM distribution schema |
| [docs/test-data.md](docs/test-data.md) | Obtaining the EBA data files used by the data-dependent tests |
| [CHANGELOG.md](CHANGELOG.md) | Release history |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Building, testing, branching strategy and pull requests |
| [docs/releasing.md](docs/releasing.md) | Versioning and the release procedure |
| [SECURITY.md](SECURITY.md) | Reporting vulnerabilities |
| [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) | Code of conduct |

## Data files

The input files (the DPM Access databases and the Annotated Table Layout workbooks, both published
by the EBA) are not part of this repository. See [docs/test-data.md](docs/test-data.md) for the
files the data-dependent tests expect and where to place them.

## License

[MIT](LICENSE). This project is not affiliated with or endorsed by the European Banking Authority.
