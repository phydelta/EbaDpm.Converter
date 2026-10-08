# EBA Annotated Table Layouts

The EBA publishes, with each release, a set of **Annotated Table Layouts**: Excel workbooks that
draw every reporting table with its rows, columns and sheets, and annotate each header and cell
with the dimensions and members that define it. They are published directly by the EBA and are
independent of the Access database, which makes them the second source of truth used to validate
the conversion (plane C, see [validation.md](validation.md)).

`--extract-layouts` reads those workbooks and writes their content into a **SQLite layout
repository** that can be queried with any SQLite tool and passed to `--validate --layouts`.

```text
EbaDpm.Converter --extract-layouts "<directory>" --out "<layouts.db>"
                 --dictionary "<converted.db>" [--overwrite]
```

```powershell
EbaDpm.Converter --extract-layouts "4.2 table layouts" --out layouts-4.2.db `
  --dictionary eba-4.2.db --overwrite
```

## Input files

The directory is searched recursively for `*.xlsx` files, so both a flat folder and a folder with
one subfolder per framework work. The framework, release and module of each workbook are taken
from its file name. Three naming formats have been used by the EBA and all three are recognised:

| Era | File name pattern | Example | Granularity |
|---|---|---|---|
| 4.2 and later | `<date> Annotated Table Layout  <FW> <REL> <MODULE><FW> <REL>` | `20260106 Annotated Table Layout  COREP 4.2 COREP_ALMCOREP 4.2.xlsx` | One workbook per module |
| 4.0 | `<date> Annotated Table Layout  <MODULE><FW> <REL>` | `20241217 Annotated Table Layout  COREP_LRCOREP 4.0.xlsx` | One workbook per module |
| 3.2 | `Annotated Table Layout <PHASE>-<FW> <REL>` | `Annotated Table Layout 320-P1-COREP 3.2.xlsx` | One workbook per framework; no module |

In the 4.0 format the module and framework are written together without a separator. They are
split by structure, not by a list of known frameworks: a sub-module starts with the framework code
followed by `_` (`COREP_LR` + `COREP`), and a module that is the framework itself appears twice in a
row (`DORADORA` → framework and module `DORA`). In the 3.2 format there is no module, and
`ModuleCode` is stored as `NULL` rather than invented.

From 4.2 onwards each module corresponds to a return type that institutions report; workbooks of
the same framework are subsets of that framework, not versions of each other.

A file whose name matches none of the three patterns stops the extraction with an error naming
the file.

## Why `--dictionary` is mandatory

Layout annotations are written as codes in parentheses. A two-term annotation such as
`(domain:member)` is explicit, but a **single-term** annotation such as `(XYZ)` is ambiguous: it
can be the **metric** of a column (a member of the `MET` domain) or a **key dimension** of an open
axis. The text alone cannot tell them apart, and the distinction changes how the cell is labelled.

The extractor resolves the ambiguity by looking the code up in a dictionary: any SQLite database in
the distribution schema, normally the output of the conversion for the same release. The
dictionary is also used to check that two-term `(domain:member)` codes exist. Without it, both
classifications would fall back to a guess that mislabels cells in a way that cannot be detected
afterwards, so the command refuses to run instead.

The dictionary's path and SHA-256 hash are recorded in the repository (`LayoutExtraction`), so a
repository always states which dictionary it was resolved against.

## The layout repository

| Table | One row per |
|---|---|
| `LayoutExtraction` | Extraction run: dictionary path and hash, timestamp (provenance) |
| `LayoutFile` | Workbook: path, `FrameworkCode`, `ModuleCode` (nullable), `ReleaseLabel` |
| `LayoutSheet` | Table sheet: `TableCode`, optional Z suffix, label. The table-of-contents sheet is skipped |
| `LayoutDeclaration` | Dimension, domain or hierarchy declared in a header region, with `IsKey` for open-axis keys |
| `LayoutOrdinate` | Ordinate of a sheet: `(SheetId, Axis, OrdinateCode)` is unique. `IsFallbackCode = 1` marks ordinates whose code is a column letter because the sheet shows no DPM code |
| `LayoutValue` | Member annotation (`DomainCode`, `MemberCode`) attached to a declaration and, where it can be linked, to an ordinate |
| `LayoutCell` | The full row × column product of a sheet's ordinates, with `DatapointId`, `DataType` and `IsShaded`. Shaded cells have no datapoint |
| `LayoutEqualityRule` | Terms of the `==` equality rules found in cell comments: cells, possibly in different tables, that must denote the same datapoint |
| `LayoutRaw` | The raw text of every annotated cell, for traceability |
| `LayoutUnparsed` | Every annotation that could not be interpreted, with a `Kind` (grammar failure, orphan value, orphan declaration, unlinked value, equality-rule grammar failure) and a `Reason` |

Nothing is silently dropped: anything the extractor cannot interpret ends up in `LayoutUnparsed`.
Some entries are benign by design — for example Z-axis values, which belong to the sheet header
rather than to an ordinate, or an open axis declared without restrictions.

At the end of a run the command prints a summary: files and sheets processed, declarations,
ordinates, values and cells written, shaded-cell rate, `LayoutUnparsed` broken down by `Kind`
(always printed, even when zero), the share of column ordinates with a real DPM code, the
value-to-ordinate link rate, and the equality rules found.

The repository is self-contained. It does not depend on this converter and can be used on its own
to answer questions such as "which dimensions and members define cell `r0010 c0020` of
`C_01.00`" with plain SQL.

## Coverage of the published layouts

The EBA publishes Annotated Table Layouts for releases 3.2 (including its point releases), 4.0
and 4.2 onwards. No layouts exist for the 2.x releases (2014–2017), so for most historical
DPM 1.0 taxonomies there is no layout to compare against, and plane C reports those tables as
excluded.
