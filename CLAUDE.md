# CLAUDE.md

Guidance for Claude Code sessions working on this repository. Human contributors should read
[CONTRIBUTING.md](CONTRIBUTING.md); this file adds how AI-assisted work is organised.

## What this project is

A .NET 10 console tool that converts the **EBA DPM Database** from Microsoft Access (`.accdb`) to
**SQLite** in the 45-table DPM distribution schema. It is a data-model transformation, not a table
dump, and it supports both source models with automatic detection: **DPM 1.0** (e.g. release 4.1)
and **DPM 2.0** (releases 4.2, 4.3...).

The goal is **timeliness**: produce the SQLite dictionary on the day the EBA publishes a release.
At that moment no other SQLite edition of that release exists, so the internal invariants
(plane A) and the comparison against the EBA Annotated Table Layouts (plane C) are the
functionality that matters; the comparison against a reference SQLite export (plane B) is only a
regression instrument.

## Sources of truth

Order of authority, in any dispute: **EBA Access database → EBA Annotated Table Layouts →
(information only) a reference SQLite export.**

- The Access database and the layouts are published by the EBA. A reference SQLite export is
  another implementation, not a specification: a difference against it is not automatically a
  defect, and it never wins a tie.
- Specifications of the formats: the EBA DPM metamodel documentation (DPM 2.0) and the EIOPA DPM
  database technical documentation (target schema). Read the documented meaning of a column
  before mapping it; never infer it from the name.
- Project documentation: [docs/mapping-dpm1.md](docs/mapping-dpm1.md),
  [docs/mapping-dpm2.md](docs/mapping-dpm2.md), [docs/source-models.md](docs/source-models.md),
  [docs/target-schema.md](docs/target-schema.md), [docs/validation.md](docs/validation.md).
  When a rule changes, the documentation changes in the same pull request.

## Roles

Work is split between the main session (orchestrator) and two subagents defined in
[.claude/agents/](.claude/agents/):

| Role | Model | Responsibility | Must not |
|---|---|---|---|
| **Orchestrator** (main session) | most capable | Talks to the user, plans, decides mapping and design questions, arbitrates, updates docs, commits | Write production code or tests itself when a subagent can |
| **`dpm-developer`** | sonnet | Production code under `src/` | Write or run tests, commit |
| **`dpm-tester`** | sonnet | Writes **and runs** tests under `tests/`, reports failures with evidence | Touch `src/`, weaken a test |

- The cycle is: developer implements → tester tests → on failure, the tester's diagnosis goes
  back to the developer. Never the other way round.
- Subagents execute a closed specification. **Any business or design decision goes back to the
  orchestrator**, and decisions the code and docs cannot settle go to the user.
- Run independent subagents **in parallel**. Give each a short, self-contained context; start a
  new agent when the topic changes instead of reusing a long one.
- Measure before deciding: derive and verify a rule on the data (see *Querying the data* below)
  before delegating its implementation. A zero that has not been shown able to be non-zero
  (positive control) is not a result.

## Repository layout

```
src/EbaDpm.Converter.Cli    command-line entry point (EbaDpm.Converter.exe)
src/EbaDpm.Converter.Core   Access readers, DPM 1.0 / DPM 2.0 mapping, SQLite writer, validation, layouts
tests/EbaDpm.Converter.Tests  xUnit tests (synthetic + data-dependent)
docs/                       public documentation
Data/                       EBA data files, NOT versioned, read-only (see docs/test-data.md)
```

## Environment

- Windows x64, .NET SDK 10 (`global.json`), `Microsoft.ACE.OLEDB.16.0` x64 installed.
- No `sqlite3` CLI: use Python's `sqlite3` module for SQLite files.
- GitHub repository `phydelta/EbaDpm.Converter`, with the `gh` CLI available.
- .NET plugins for Claude Code, from the `dotnet/skills` marketplace, enabled in
  `.claude/settings.json`: `dotnet` (C# language server and `csharp-refactoring`), `dotnet-test`,
  `dotnet-msbuild` and `dotnet-diag`. Enabling them in the committed settings does not download
  them. Each collaborator installs them once:
  ```powershell
  foreach ($p in 'dotnet','dotnet-test','dotnet-msbuild','dotnet-diag') {
      claude plugin install "$p@dotnet-agent-skills" --scope project
  }
  ```
  The agents in `.claude/agents/` list which skills to use and when; the project rules in this
  file win over any skill.

## Commands

```powershell
dotnet build                                   # warnings are errors

# Fast loop (~15 s): data tests are skipped when the data directory does not exist
$env:EBADPM_TEST_DATA = 'none'; dotnet test

# Full suite (~9 min): uses ./Data, or EBADPM_TEST_DATA when set to a real directory
Remove-Item Env:EBADPM_TEST_DATA -ErrorAction Ignore; dotnet test
```

- Iterate with the fast loop or a `--filter`. Run the full suite **once per cycle**, after
  grouping the changes in flight, and before any pull request that touches conversion or
  validation. Do not rerun it on unchanged code.
- Test collections run sequentially: ACE OLEDB is not safe for concurrent use inside one process.
  Real concurrency over Access is only possible across processes.

## Querying the data

- SQLite (outputs, reference exports, layout repositories): `python -c "import sqlite3; ..."`.
- Access: PowerShell with `System.Data.OleDb` and `Provider=Microsoft.ACE.OLEDB.16.0`. In OLEDB
  `LIKE`, `_` matches one character; filter prefixes with `LEFT`/`InStr`. In PowerShell use
  `COUNT(1)` rather than `COUNT(*)`.
- Never modify anything under `Data/`.

## Rules that must not be broken

- **Everything in the repository is in English**: code, comments, messages, docs, commit
  messages. Target-schema and Access names are literal (`mConcept`, `TaxonomyID`...): never
  renamed or "improved".
- **No vendor names** for the producer of reference SQLite exports. Refer to them as "a reference
  SQLite export" or "a third-party export"; the only organisations named are the EBA and EIOPA.
- **No invented data**: emit what the Access database declares. Do not copy values from a
  reference export that the source does not contain.
- **Compare by business key**, never by surrogate ID: IDs differ between source, output and
  references.
- **Known exceptions are never relaxations.** An exception names one object by its business key,
  for one check, with its reason (`src/EbaDpm.Converter.Core/Validation/KnownExceptions.cs` and
  `Resources/plane-c-known-divergences-<release>.tsv`). Never lower a threshold or exclude by
  pattern. A stale exception must fail.
- No figure measured on one release is a property of the format: frameworks change between
  releases. Derive values from the data, or date and scope them explicitly.
- Validation rules (`vValidationRule*` tables) are out of scope and stay empty.

## Git and releases

- `main` is protected: all changes go through a branch and a pull request with green CI and
  CodeQL; squash merge. Branch names: `feature/`, `fix/`, `docs/`, `ci/`, `deps/`, `release/X.Y.Z`.
- Only the orchestrator commits or pushes, and only when the user asks. Never force-push, never
  push to `main` directly, never move or delete a `v*` tag.
- Releases follow [docs/releasing.md](docs/releasing.md): a `release/X.Y.Z` pull request bumps
  `<Version>` in `Directory.Build.props` and dates the `CHANGELOG.md` section; the release itself
  is created by the *Release* workflow, run by a maintainer.
- User-visible changes get an entry under `## [Unreleased]` in `CHANGELOG.md`.
