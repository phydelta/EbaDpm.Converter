# Test data

The test project has two kinds of tests:

- **Synthetic tests** build their own small inputs (in-memory SQLite databases, generated
  workbooks, empty `.accdb` files) and need nothing else. They are what `dotnet test` runs on a
  fresh clone.
- **Data tests** run the converter on the real EBA publications. They are marked
  `[DataFact]` / `[DataTheory]` and are **skipped** unless a data directory is available.

```powershell
dotnet test                      # synthetic tests only; data tests reported as skipped
```

## Enabling the data tests

The data directory is looked up in this order:

1. the directory named by the environment variable **`EBADPM_TEST_DATA`**;
2. a folder named **`Data/`** at the repository root (next to `EbaDpm.Converter.sln`).

Either way, the directory must have the layout described below.

```powershell
$env:EBADPM_TEST_DATA = "D:\ebadpm-data"
dotnet test
```

Requirements:

- **Windows x64** with the **Microsoft Access Database Engine (ACE OLEDB 16.0), 64-bit**
  installed — the same requirement as the converter itself. Without it, no `.accdb` can be
  opened.
- About **2 GB** of free disk space: the Access databases are several hundred MB each, and the
  tests write full conversions to temporary files.
- Time: the full data suite takes **several minutes**, because several fixtures convert entire
  databases and validate the result.

The data files are not part of the repository and must never be committed (they are large and
are published by the EBA, not by this project).

## Expected layout

File and folder names must match **exactly** — the tests look them up by name.

```
<data directory>/
├─ DPM 1.0 Database_v4_1_20250709.accdb
├─ DPM2 Database_v 4_2_20251125.accdb
├─ DPM2 Database_v 4_3_20260622.accdb
├─ 3.2 table layouts/
│    └─ *.xlsx
├─ 4.2 table layouts/
│    └─ *.xlsx                (subfolders are fine; the folder is searched recursively)
├─ 4.3 table layouts/
│    └─ *.xlsx
├─ EBA_3.2_phase_1.db
├─ EBA_4.0_ERRATA_5.db
├─ EBA_4.2_Hotfix.db
└─ DPM Database 2.1.0.1.accdb   (optional)
```

| Name | What it is | Used for |
|---|---|---|
| `DPM 1.0 Database_v4_1_20250709.accdb` | EBA DPM database, DPM 1.0 model, release 4.1 | DPM 1.0 conversion tests (dictionary, templates, axes and cells, signatures, modules, integrity) |
| `DPM2 Database_v 4_2_20251125.accdb` | EBA DPM database, DPM 2.0 model, release 4.2 | DPM 2.0 conversion tests (reader, skeleton, full pipeline, validation) |
| `DPM2 Database_v 4_3_20260622.accdb` | EBA DPM database, DPM 2.0 model, release 4.3 | Cutoff-release derivation on a later publication (it must resolve to `4.3`), and validation of a 4.3 conversion |
| `3.2 table layouts/` | EBA Annotated Table Layouts, release 3.2 | Layout extraction of the DPM 1.0 era (file names without date or module), and the layout-based checks on DPM 1.0 output |
| `4.2 table layouts/` | EBA Annotated Table Layouts, release 4.2 | Layout extraction (with and without dictionary) and plane C comparison of the DPM 2.0 4.2 conversion |
| `4.3 table layouts/` | EBA Annotated Table Layouts, release 4.3 | Layout extraction on the 4.3 publication |
| `EBA_3.2_phase_1.db`, `EBA_4.0_ERRATA_5.db`, `EBA_4.2_Hotfix.db` | SQLite editions of the DPM dictionary in the distribution schema, produced outside this project | Regression comparisons (plane B) and tests that check the converter's schema and conventions against an independent export. They are not needed to *run* the converter, but the data-dependent test suite expects them: if the data directory exists and one of them is missing, the tests that use it fail with a message naming the file. |
| `DPM Database 2.1.0.1.accdb` | An old (2014) EBA DPM database with an earlier DPM 1.0 schema | **Optional.** Only a source-model detection test uses it, and that test is skipped if the file is missing. |

What the planes of validation are, and why the reference SQLite databases are an instrument and
not a source of truth, is explained in [validation.md](validation.md). How the layouts are read
is described in [table-layouts.md](table-layouts.md).

## Downloading the EBA files

The DPM databases and the Annotated Table Layouts are published by the EBA on the page of each
reporting framework release, for example:

- Reporting framework 4.0:
  <https://www.eba.europa.eu/risk-and-data-analysis/reporting/reporting-frameworks/reporting-framework-40>
- Reporting framework 4.3:
  <https://www.eba.europa.eu/risk-and-data-analysis/reporting/reporting-frameworks/reporting-framework-43>

The pages for 3.2, 4.1 and 4.2 follow the same pattern (`reporting-framework-32`,
`reporting-framework-41`, `reporting-framework-42`). Page addresses and package names are
controlled by the EBA and may change; if a link has moved, search the EBA site for
"reporting framework" and the release number.

On each page:

1. Download the **DPM database** package (the Microsoft Access version).
   - Release **4.1** → the DPM 1.0 database.
   - Releases **4.2** and **4.3** → the DPM 2.0 databases.
2. Download the **Annotated table layouts** package for releases **3.2**, **4.2** and **4.3**.
3. Unzip each package.
4. Place the files in the data directory:
   - Give each `.accdb` the exact name shown in the table above, renaming it if the downloaded
     file is named differently; otherwise the tests will not find it.
   - Copy the layout workbooks (`.xlsx`) into the matching `<release> table layouts` folder. The
     folder is searched recursively, so the unzipped folder structure can be kept.

Tests assert properties of these specific editions (for example the derived cutoff release of
each DPM 2.0 file), so a different edition renamed to an expected name may make some data tests
fail even though the converter handles it correctly.

The three reference SQLite databases are not published by the EBA and are not distributed with
this project. The converter never needs them; only the data-dependent tests that compare against
an independent export do. Without them those tests fail (they do not skip), while the
synthetic suite is unaffected.
