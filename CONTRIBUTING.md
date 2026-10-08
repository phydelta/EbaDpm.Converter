# Contributing

Contributions are welcome: bug reports, mapping corrections backed by evidence, support for new
EBA releases, documentation and tests.

## How to contribute

1. For anything beyond a small fix, **open an issue first** so the approach can be agreed before
   you invest time. Conversion discrepancies have their own issue form: please include the
   evidence from the Access database or the EBA layout.
2. **Fork** the repository (external contributors) or create a branch (maintainers).
3. Make the change on a short-lived branch, with tests.
4. Open a **pull request against `main`** and fill in the template.
5. CI must be green and a code owner must approve. The pull request is **squash-merged**, so its
   title becomes the commit message on `main`: write it in the imperative, e.g.
   `Emit table-wide context pairs as categorisation`.

### Branching strategy

The project uses a trunk-based flow (GitHub flow):

| Branch | Purpose |
|---|---|
| `main` | Always releasable. Protected: changes only through reviewed pull requests with green CI; no direct pushes, no force pushes, linear history |
| `feature/<topic>` | New capabilities |
| `fix/<topic>` | Bug and conversion fixes |
| `docs/<topic>`, `ci/<topic>`, `deps/<topic>` | Documentation, CI and dependency changes |
| `release/X.Y.Z` | The pull request that bumps the version and dates the changelog |

Branches are short-lived and deleted after merging. There are no long-lived `develop` or
maintenance branches: fixes go to `main` and ship in the next release. Releases are tags
(`vX.Y.Z`) created by the release workflow, never by hand; see [docs/releasing.md](docs/releasing.md).

### Continuous integration

Every pull request and every push to `main` runs:

- **CI** — restore, build (warnings are errors) and the fast test suite on Windows.
- **CodeQL** — static security and quality analysis of the C# code (also run weekly).

CI has no access to the EBA data files, so it cannot run the data-dependent suite. If your change
touches conversion or validation, run that suite locally and report the result in the pull request.

## Prerequisites

- Windows x64
- .NET 10 SDK
- Microsoft Access Database Engine 2016 Redistributable (x64), which provides
  `Microsoft.ACE.OLEDB.16.0` — needed to run the converter and the data-dependent tests

## Build

```powershell
dotnet build EbaDpm.Converter.sln
```

Warnings are treated as errors.

## Test

The test project is `tests/EbaDpm.Converter.Tests` (xUnit). Tests fall into two groups:

| Suite | Needs | How to run |
|---|---|---|
| **Fast suite** — unit tests, schema tests, synthetic fixtures | The SDK (two tests also need ACE OLEDB and are skipped without it) | `dotnet test` with no data directory |
| **Data-dependent suite** — conversions of the real EBA databases, comparisons against layouts and reference exports | The EBA data files and ACE OLEDB | `dotnet test` with `EBADPM_TEST_DATA` set (or `./Data` present) |

The data-dependent tests look for the data files in the directory given by the `EBADPM_TEST_DATA`
environment variable, or in `./Data` at the repository root when it is not set. When the
directory is missing, those tests are reported as skipped with a message pointing to
[docs/test-data.md](docs/test-data.md), which lists the files they expect.
`./scripts/Initialize-TestData.ps1` sets up `./Data` in one step.

```powershell
$env:EBADPM_TEST_DATA = "D:\eba-data"
dotnet test
```

The full suite converts several large Access databases and takes several minutes. Use the fast
suite while iterating and run the full suite before opening a pull request that touches the
conversion or the validation.

Test collections run sequentially (`xunit.runner.json`): the ACE OLEDB provider is not safe for
concurrent use within one process. Do not enable parallel collections.

Never modify the data files; tests treat them as read-only.

## Coding conventions

- **English** for code, identifiers, comments, messages and documentation.
- **Target schema names are literal.** Table and column names of the distribution schema
  (`mConcept`, `TaxonomyID`, `mOrdinateCategorisation`...) and of the Access source are used
  exactly as defined, never renamed or "improved". The same applies to DPM codes (`MET`, `qTR`,
  `COREP 4.2`...) and to property keys stored in the output.
- **The schema DDL is a contract.** Changes to
  [`dpm-distribution-schema.sql`](src/EbaDpm.Converter.Core/Resources/dpm-distribution-schema.sql)
  change the output format and need a strong justification.
- **Read the semantics, do not infer them from names.** Before changing how a table is mapped,
  check the documented meaning of its columns ([docs/target-schema.md](docs/target-schema.md),
  [docs/source-models.md](docs/source-models.md)).
- **No invented data.** Emit what the Access database declares. A difference against a third-party
  reference export is not, by itself, a reason to change the output: the Access database and the
  EBA Annotated Table Layouts are the sources of truth.
- **Large tables are streamed** with `OleDbDataReader` and written with `SqliteBatchWriter`; do not
  load whole source tables into memory.
- **Compare by business key** in tests and checks, never by surrogate ID.
- Follow the existing style of the surrounding code; keep comments focused on *why* a rule exists.

## Validation checks and known exceptions

- New invariants belong in plane A (`src/EbaDpm.Converter.Core/Validation/Checks`), with a stable
  check code (`A-XXX-nn`) and a clear statement.
- A known exception must name **one object by its exact business key**, for one specific check,
  with the reason and the evidence. Never relax a threshold or exclude by pattern.
- For a new EBA release, regenerate the plane C known-divergences file for that release instead of
  editing the existing one. See [docs/validation.md](docs/validation.md#plane-c-known-divergences).

## Pull requests

- Keep each pull request focused on one change: one mapping rule, one group of tables, one check
  family.
- Include tests. A mapping fix should come with a test that fails without it, keyed by business
  key; a new check should come with a positive control showing it can detect the problem.
- Explain the evidence: which Access rows, layout cells or metamodel definitions justify the
  change. Quote measured effects (rows added or removed per table) when the output changes.
- Make sure `dotnet build` passes without warnings and the fast suite passes. If the change
  affects conversion or validation, run the full suite and state the result.
- State whether the change modifies the output for DPM 1.0, DPM 2.0 or both.
- Update the documentation, and add an entry under `## [Unreleased]` in `CHANGELOG.md` when
  behaviour changes.

## Reporting issues

Use the issue forms. Security problems must be reported privately, as described in
[SECURITY.md](SECURITY.md). Participation is governed by the [code of conduct](CODE_OF_CONDUCT.md).

For bug reports, please include the tool version, the exact command line, the source file name
(EBA release), the console output and, for validation issues, the JSON report produced with
`--report`. Do not attach the EBA data files; refer to them by name and release.
