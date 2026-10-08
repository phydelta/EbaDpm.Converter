# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/).

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
  (`B-OCA-01`) passes for all 18,556 compared ordinates, and plane B critical differences drop from
  9 to 4.

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
