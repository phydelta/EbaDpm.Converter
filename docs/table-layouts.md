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
from its file name. Four naming formats have been used by the EBA and all four are recognised:

| Era | File name pattern | Example | Granularity |
|---|---|---|---|
| 4.4 | `<date> <MODULE>_table layout <REL>` | `20261007 AML_ELIGIBILITY_table layout 4.4.xlsx` | One workbook per module; no framework in the name |
| 4.2 and 4.3 | `<date> Annotated Table Layout  <FW> <REL> <MODULE><FW> <REL>` | `20260106 Annotated Table Layout  COREP 4.2 COREP_ALMCOREP 4.2.xlsx` | One workbook per module |
| 4.0 | `<date> Annotated Table Layout  <MODULE><FW> <REL>` | `20241217 Annotated Table Layout  COREP_LRCOREP 4.0.xlsx` | One workbook per module |
| 3.2 | `Annotated Table Layout <PHASE>-<FW> <REL>` | `Annotated Table Layout 320-P1-COREP 3.2.xlsx` | One workbook per framework; no module |

In the 4.0 format the module and framework are written together without a separator. They are
split by structure, not by a list of known frameworks: a sub-module starts with the framework code
followed by `_` (`COREP_LR` + `COREP`), and a module that is the framework itself appears twice in a
row (`DORADORA` → framework and module `DORA`). In the 3.2 format there is no module, and
`ModuleCode` is stored as `NULL` rather than invented.

In the 4.4 format the module code may contain `_`, so the name is anchored on the literal
`_table layout `. The framework is not in the name: it is resolved from the module code through
the dictionary (`mModule.ModuleCode`, then its taxonomy and `mReportingFramework.FrameworkCode`),
case-insensitively. Without a dictionary, or with a module code that the dictionary does not
contain, the extraction stops with an error that names the file and the code; the framework is
never guessed. The shading of a cell is read from its fill colour without the alpha byte: the
grey is `FFD8D8D8` up to 4.3 and `00D8D8D8` from 4.4, `999999` (4.4; the legacy palette index 55
before) and `595959` (4.4) are also shaded: every `999999` cell (1463/1463) and the single `595959`
grid cell (K_41.00 r0610 c0080) are `IsExcluded` in the Access 4.4 database. The other 4.4 fills
(`FFFF00`, `C4D79B`) are not shaded (measured 2026-11-20 on the 4.3 and 4.4 corpora). Relationship
targets inside the workbook are resolved per OPC (4.4 writes absolute part names such as
`/xl/worksheets/sheet1.xml`; 4.3 wrote relative ones), a comments relationship whose part does not
exist is an error, and a Z-sheet name may have a space before the parenthesis (`K_74.00.a (0010)`). The workbook's index sheet is called `TOC` up to 4.3 and `Index` from 4.4; both are
skipped.

From 4.2 onwards each module corresponds to a return type that institutions report; workbooks of
the same framework are subsets of that framework, not versions of each other.

A file whose name matches none of the four patterns stops the extraction with an error naming
the file.

## Why `--dictionary` is mandatory

Layout annotations are written as codes in parentheses. A two-term annotation such as
`(domain:member)` is explicit, but a **single-term** annotation such as `(XYZ)` is ambiguous: it
can be the **metric** of a column (a member of the `MET` domain) or a **key dimension** of an open
axis. The text alone cannot tell them apart, and the distinction changes how the cell is labelled.

The extractor resolves the ambiguity by looking the code up in a dictionary: any SQLite database
converted by this tool, either a 45-table distribution output (normally the conversion of the same
release) or a native DPM 2.0 output (see below). The
dictionary is also used to check that two-term `(domain:member)` codes exist. Without it, both
classifications would fall back to a guess that mislabels cells in a way that cannot be detected
afterwards, so the command refuses to run instead.

The dictionary's path and SHA-256 hash are recorded in the repository (`LayoutExtraction`), so a
repository always states which dictionary it was resolved against.

### Dictionary from a native output

`--dictionary` also accepts a native DPM 2.0 output ([target-schema-native.md](target-schema-native.md)),
recognised by its catalogue (`Category` and `TableVersionCell`, no `mModule`). The native file holds
every release, so the dictionary is the union of the history of the file, as the dictionary of the
latest 45-table release is; it is built from the native tables with the rules of the 45-table mapping,
and only as far as the extractor needs:

- **Metrics** (members of `MET`): the codes (every `ItemCategory` row) of the `_PR` items that are
  metrics: the property of a `fact` variable (`VariableVersion.PropertyID` of a `Variable` with
  `Type = 'fact'`), or of a non-key header (`HeaderVersion.PropertyID`, `Header.IsKey = 0`), or whose
  own code matches `^[A-Za-z]{2}[0-9]+$`.
- **Dimensions**: the properties used in `ContextComposition.PropertyID` or as the property of a `key`
  variable, with each of their codes. The domain code is the `Category.Code` of the `PropertyCategory`
  row whose release window overlaps the one of the code (the first row when none does); `_NA` is
  `INT_NA` for data type `i` and `STR_NA` for `s`. As in the 45-table mapping, the metric dimension
  `MET` is added, and a property whose values live in a structural category (`_TE`: `TEM`) is not a
  dimension.
- **(Domain, member) pairs**: every `ItemCategory` row whose category is not `SE`, `_NA`, `_PR` or `_TE`,
  with the `Category.Code` as domain, plus (`MET`, metric code).
- **Module to framework** (layout names that do not carry the framework, 4.4): `ModuleVersion.Code` ->
  `Module.FrameworkID` -> `Framework.Code`, without document modules, case-insensitively. A module that
  maps to more than one framework is an error.

On the 4.4 database the repository extracted with the native dictionary is identical, table by table,
to the one extracted with the 45-table dictionary of release 4.4 (all `Layout*` tables except
`LayoutExtraction`). The two can differ for layouts of an older release, because the native dictionary
does not know the release of the layout: a code that is a dimension in 4.2 and a metric in a later
release (`qINC`, `qNCO`, `qCCP`) resolves as a metric. Measured on 2026-11-25 with the 4.4 file and
the 4.2 layouts: 10 of 11,515 declarations differ (for example `C_27.00` `D9` `qINC`). To extract
the layouts of a release before 4.4, use a 45-table dictionary of that release.

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
