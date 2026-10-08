# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## 1.2.0 - 2026-10-08

### Added

- `--cutoff-release <code>`: converts a DPM 2.0 database as of an earlier release, selected by
  its `Release.Code` (for example `--cutoff-release 4.2` on the 4.2.1 database). Also accepted by
  `--list-taxonomies`.
- `scripts/Initialize-TestData.ps1`: sets up the data directory of the data-dependent tests in
  one step, downloading the EBA DPM databases and Annotated Table Layouts and extracting the
  reference SQLite exports (see `docs/test-data.md`).
- The reference SQLite exports used by the regression tests are versioned, compressed, in
  `tests/reference-exports/`.

### Changed

- DPM 2.0: `mTaxonomy.TaxonomyLabel`, `Version`, `PublicationDate`, `FromDate` and `ToDate` are
  now derived from `Release` and `ModuleVersion` instead of being `NULL`; the last taxonomy of a
  framework is open with `ToDate` `9999-12-31` (#11).
- Validation: removed the known exception E-1 (dimension `EXC` against the 4.2 reference). The
  output has contained both versions of `EXC` for some time, so the exception was stale; a
  test now fails if any plane B exception for the 4.2 reference goes stale.
- Validation: new known exception DD-25 for the label of module `CODIS`, which the EBA corrected
  in the 4.2.1 database while the 4.2 reference export keeps the earlier text.
- Validation: new known exception DD-26 for dimension `TNS`, which the 4.2 reference export keeps
  although nothing in the DPM 2.0 database uses it. The converter still emits only used
  dimensions (#10).
- Validation: every check that applies known exceptions now reports their outcome. Before, the
  plane B exceptions DD-16 and E-7 and the plane A anomalies AO-1 and AO-3 were applied but
  reported as matching nothing, so a stale one went unnoticed; a stale AO-1 now fails plane A as
  documented. Plane B containment checks now also apply declared divergences, not only reference
  defects.
- DPM 2.0: `mRelease.IsCurrent` marks the cutoff release. With the default cutoff the output is
  unchanged; with `--cutoff-release` the requested release is the current one.
- The data-dependent tests use the DPM 2.0 database 4.2.1 (2026-02-27), the 4.2 edition the EBA
  now publishes, instead of the 2025-11-25 edition.

## 1.1.1 - 2026-10-08

First public release on GitHub. The conversion output is unchanged from 1.1.0.

### Added

- GitHub collaboration infrastructure: CI and CodeQL workflows, on-demand release workflow with
  checksums and build provenance, issue and pull request templates, security policy and code of
  conduct.
- Release executables are published as `EbaDpm.Converter-X.Y.Z-win-x64.zip`, with a
  `SHA256SUMS.txt` file and a build provenance attestation.
- The test suite runs without the EBA data files: data-dependent tests are skipped unless
  `EBADPM_TEST_DATA` (or `./Data`) points to them, and the tests that need the Access Database
  Engine are skipped when it is not installed. See
  [docs/test-data.md](https://github.com/phydelta/EbaDpm.Converter/blob/main/docs/test-data.md).

### Changed

- Source code, messages and documentation are now in English.
- Updated `Microsoft.Data.Sqlite` and `System.Data.OleDb` to 10.0.12.
- Updated test dependencies: `Microsoft.NET.Test.Sdk` 18.10.1, `xunit.runner.visualstudio` 4.0.0,
  `coverlet.collector` 10.1.0.

## 1.1.0 - 2026-09-08

### Fixed

- **Concrete tables of an abstract table were not linked to the module.** When a module declares
  an abstract table, its concrete child tables are now linked to that module as well. In the 4.2
  output this adds table `C_34.02.b` to the `IF_CLASS2` module, where the EBA layout also places it
  (`mTable` 846 → 847, with its 3 axes, 33 ordinates, 440 cells and 1,320 cell positions). No change
  for 4.3, whose Access database already declares the link.
- **Table-wide context pairs were not emitted.** Dimension-member pairs declared as context for a
  whole table (`TableVersion.ContextID` in DPM 2.0) were only used to decide axis ownership, never
  emitted as categorisation pairs. They are now emitted on the closed ordinates of the owning axis:
  +255 `mOrdinateCategorisation` rows for 4.2 and +510 for 4.3, none of them on an ordinate with a
  live cell. With this fix the ordinate categorisation comparison against the 4.2 reference export
  (`B-OCA-01`) passes for all 18,556 compared ordinates; plane B now reports
  4 critical differences.

### Not changed, by decision

- 58 descriptions of hierarchy nodes present in the 4.2 reference export are still not emitted.
  Emitting them would require copying legal citations that the Access database does not declare.
  The difference is documented as a permanent, declared divergence.

### Notes

- Some tables that matched the reference by row count in 1.0.0 now differ by one (`mTable`,
  `mTaxonomyTable`, `mTemplateOrTable`). This is not a regression: 1.0.0 had one table more and one
  fewer than the reference, which cancelled out; fixing the missing one exposes the other, which
  the Access database declares. See [docs/validation.md](docs/validation.md#fidelity-against-the-42-reference-export).

## 1.0.0 - 2026-09-08

First release.

### Added

- Conversion of the EBA DPM Access database into SQLite with the 45-table DPM distribution schema.
- Support for **DPM 1.0** sources (validated on release 4.1) and **DPM 2.0** sources (validated on
  releases 4.2 and 4.3), with automatic detection of the source model from the database catalog.
- Mandatory taxonomy selection: `--all`, `--taxonomies`, `--taxonomykeys` or `--releases`.
- `--list-taxonomies` to inspect a source and `--schema-only` to create the empty schema.
- `--validate` with three planes: internal invariants (plane A), informational comparison against a
  reference SQLite export (plane B, never gating) and signature-by-signature comparison against the
  EBA Annotated Table Layouts (plane C). JSON report with `--report`.
- `--extract-layouts` to build a queryable SQLite repository from the EBA Annotated Table Layout
  workbooks, supporting the 3.2, 4.0 and 4.2+ file-name formats.
- Known-exception mechanism that excludes named objects by exact business key, with stale
  exceptions reported as failures.

### Highlights of the validation work before release

- Default members are exported as the Access database declares them.
- Several categorisation losses found before release were fixed: the fixed pairs of open-axis
  ordinates (DPM 1.0), shaded ordinates without context, and the fixed pairs of key headers
  (DPM 2.0).
- Plane C runs on both source models: 0 violations on DPM 1.0 against the 3.2 and 4.0 layouts
  (108,307 signatures) and on DPM 2.0 4.2 against the 4.2 layouts (90,972 signatures).

### Known limitations

- DPM validation rules are not exported: `vValidationRuleExpressions` and `vValidationRuleTables`
  are created empty.
- Windows x64 only, because of `Microsoft.ACE.OLEDB.16.0`.
