# Test data

The test project has two kinds of tests:

- **Synthetic tests** build their own small inputs (in-memory SQLite databases, generated
  workbooks, empty `.accdb` files) and need nothing else, except two tests that create `.accdb`
  files and are skipped (`[AceFact]`) when the ACE OLEDB provider is not installed. They are what
  `dotnet test` runs on a fresh clone.
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
├─ DPM2 Database_v 4_2_1.accdb
├─ DPM2 Database_v 4_3_20260622.accdb
├─ 3.2 table layouts/
│    └─ *.xlsx
├─ 4.2 table layouts/
│    └─ *.xlsx                (subfolders are fine; the folder is searched recursively)
├─ 4.3 table layouts/
│    └─ *.xlsx
├─ EBA_3.2_phase_1.db
├─ EBA_4.0_ERRATA_5.db
└─ EBA_4.2_Hotfix.db
```

| Name | What it is | Used for |
|---|---|---|
| `DPM 1.0 Database_v4_1_20250709.accdb` | EBA DPM database, DPM 1.0 model, release 4.1 | DPM 1.0 conversion tests (dictionary, templates, axes and cells, signatures, modules, integrity) |
| `DPM2 Database_v 4_2_1.accdb` | EBA DPM database, DPM 2.0 model, release 4.2.1 (2026-02-27) | DPM 2.0 conversion tests (reader, skeleton, full pipeline, validation) |
| `DPM2 Database_v 4_3_20260622.accdb` | EBA DPM database, DPM 2.0 model, release 4.3 | Cutoff-release derivation on a later publication (it must resolve to `4.3`), and validation of a 4.3 conversion |
| `3.2 table layouts/` | EBA Annotated Table Layouts, release 3.2 | Layout extraction of the DPM 1.0 era (file names without date or module), and the layout-based checks on DPM 1.0 output |
| `4.2 table layouts/` | EBA Annotated Table Layouts, release 4.2 | Layout extraction (with and without dictionary) and plane C comparison of the DPM 2.0 4.2 conversion |
| `4.3 table layouts/` | EBA Annotated Table Layouts, release 4.3 | Layout extraction on the 4.3 publication |
| `EBA_3.2_phase_1.db`, `EBA_4.0_ERRATA_5.db`, `EBA_4.2_Hotfix.db` | SQLite editions of the DPM dictionary in the distribution schema, produced outside this project and shipped compressed in [`tests/reference-exports/`](../tests/reference-exports/) | Regression comparisons (plane B) and tests that check the converter's schema and conventions against an independent export. They are not needed to *run* the converter, but the data-dependent test suite expects them: if the data directory exists and one of them is missing, the tests that use it fail with a message naming the file. |

What the planes of validation are, and why the reference SQLite databases are an instrument and
not a source of truth, is explained in [validation.md](validation.md). How the layouts are read
is described in [table-layouts.md](table-layouts.md).

## Setting up the data directory

The script `scripts/Initialize-TestData.ps1` downloads the EBA publications, extracts the
reference SQLite exports shipped with the repository, and lays everything out under the
expected names:

```powershell
./scripts/Initialize-TestData.ps1                               # into ./Data
./scripts/Initialize-TestData.ps1 -DataDirectory D:\ebadpm-data # anywhere else
```

- It is idempotent: what is already in place is left untouched (`-Force` downloads again).
- `-SkipDatabases` fetches only the table layouts (about 30 MB instead of about 500 MB).
- It warns when the ACE OLEDB provider is not registered.
- It ends with a summary of every file the tests use, and exits with a non-zero code if a
  download fails.

Creating the data directory switches the data tests on: from then on, a missing file makes the
tests that use it fail rather than skip.

### Download addresses

The script downloads these packages, published by the EBA (dated 2026-10-08; the EBA controls
the addresses and may move them):

| Item | Package |
|---|---|
| DPM 1.0 database, release 4.1 | [DPM 1.0 Database_v4_1_20250709.accdb_.zip](https://www.eba.europa.eu/sites/default/files/2025-07/cf160041-aa49-41d3-af2b-d7e840158d65/DPM%201.0%20Database_v4_1_20250709.accdb_.zip) |
| DPM 2.0 database, release 4.2.1 | [DPM2 Database_v_4_2_1.zip](https://www.eba.europa.eu/sites/default/files/2026-02/ad0d2577-a1eb-4826-a249-a1f7701c6796/DPM2%20Database_v_4_2_1.zip) |
| DPM 2.0 database, release 4.3 | [DPM2 Database_v 4_3.zip](https://ebprstaewspublic01.blob.core.windows.net/public/tools-prod/documents/Big_Files/files/Reporting%20framework%204.3/DPM2%20Database_v%204_3.zip) |
| Table layouts, release 3.2 | [3.2 table layouts.zip](https://www.eba.europa.eu/sites/default/files/2023-11/7e727bbe-83e4-4faa-938e-7580a29d5482/3.2%20table%20layouts.zip) |
| Table layouts, release 4.2 | [260106 Annotated templates_with_finpre9dp_4.2.1.zip](https://www.eba.europa.eu/sites/default/files/2026-02/c92d5e2b-bcd7-4767-ab5b-cdf0b1737368/260106%20Annotated%20templates_with_finpre9dp_4.2.1.zip) |
| Table layouts, release 4.3 | [c. DPM Table Layouts and data point categorisation.zip](https://www.eba.europa.eu/sites/default/files/2026-07/71f710c3-111a-4976-9731-bc9bdaa2036c/c.%20DPM%20Table%20Layouts%20and%20data%20point%20categorisation.zip) |

### Reference SQLite exports

`EBA_3.2_phase_1.db`, `EBA_4.0_ERRATA_5.db` and `EBA_4.2_Hotfix.db` are not published by the
EBA and cannot be downloaded. They are versioned in
[`tests/reference-exports/`](../tests/reference-exports/), one zip per file, and the script
extracts them. They are an instrument for regression comparisons (plane B), never a source of
truth (see [validation.md](validation.md)).

### Downloading by hand

If a link has moved, the DPM databases and the Annotated Table Layouts are published by the EBA
on the page of each reporting framework release, for example:

- Reporting framework 4.0:
  <https://www.eba.europa.eu/risk-and-data-analysis/reporting/reporting-frameworks/reporting-framework-40>
- Reporting framework 4.3:
  <https://www.eba.europa.eu/risk-and-data-analysis/reporting/reporting-frameworks/reporting-framework-43>

The pages for 3.2, 4.1 and 4.2 follow the same pattern (`reporting-framework-32`,
`reporting-framework-41`, `reporting-framework-42`). If a page has moved, search the EBA site
for "reporting framework" and the release number.

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

The converter never needs the three reference SQLite databases; only the data-dependent tests
that compare against an independent export do. Without them those tests fail (they do not
skip), while the synthetic suite is unaffected.
