# Target schema: native DPM 2.0 output

Besides the 45-table distribution schema ([target-schema.md](target-schema.md)), the converter can
produce a **native DPM 2.0 output** (`--target native`, see [usage.md](usage.md)): an image of the
DPM 2.0 Access database in SQLite, table for table, with the same table and column names, the same
rows, the same IDs and the same values. Nothing is derived, renumbered or arbitrated, and nothing is
added that the Access database does not declare.

This output exists only for DPM 2.0 sources. Its code lives in `src/EbaDpm.Converter.Native`, which
depends on nothing of the 45-table pipeline ([architecture.md](architecture.md#removal-path)).

## Ownership: the schema is the EBA's

For the 45-table output the project owns the schema and decides how DPM 2.0 is adapted to it. For
the native output it does not: the schema is the one the EBA publishes in the Access database, and
the converter follows it.

- The DDL is **built from the catalogue of the Access database at conversion time** (tables,
  columns, types, nullability, primary keys, relationships, indexes), with the fixed type mapping
  below. It is never a hand-maintained copy that would block a conversion on publication day.
- **Baseline: the 4.4 Access database** (`DPM2 Database_v 4_4_20261006.accdb`), which contains the
  whole history from release 3.4. Its catalogue is embedded in the assembly
  (`src/EbaDpm.Converter.Native/Resources/native-baseline-4.4.json`). It is what `--schema-only
  --target native` creates, and what a test checks against the catalogue of the 4.4 database.
- **Schema drift.** A later publication whose catalogue differs from the baseline converts with
  its own catalogue; the differences (tables and columns added, removed or retyped, primary keys,
  relationships) are reported in the conversion summary and by `--validate`, and never fail the
  conversion. The consequences of each EBA change are assessed case by case when it appears
  ([validation.md](validation.md#native-output)).
- **Publications older than the baseline are not accepted.** The native output starts from the
  4.4 schema: a source whose `Release` table has no row with `Code = '4.4'` (the baseline release)
  is rejected with an error. The 4.2.1 and 4.3 databases are therefore rejected; a later
  publication carries release 4.4 in its history and is accepted. (Decided 2026-11-25, #98. Their
  physical schema differs from the baseline, and the 4.4 database already contains their
  releases.)

## Content

Every user table of the Access database is exported, except a **closed list of 11 named tables**
(never a pattern):

| Excluded | Tables | Reason |
|---|---|---|
| Change log | `ChangeLog`, `ChangeLogAttribute`, `User`, `Role`, `UserRole` | Declared non-normative by the DPM metamodel documentation (section 4.2.5); its user data is for internal use only |
| EBA working tables | `Aux_CellMapping`, `Aux_CellStatus`, `ModelViolations`, `VarGeneration_Detail`, `VarGeneration_Summary`, `VariableGeneration` | Not part of the metamodel |

Access system tables (`MSys*`) and temporary tables (names starting with `~`) are not user tables
and are never exported. Empty tables are created empty.

On the 4.4 database this gives **59 tables**: 47 of the metamodel and the 12 validation-rule
tables. The table list is that of the catalogue of the database converted, so a table the EBA adds
in a later publication is exported, and reported as drift.

### Validation rules: in, and in development

The 12 validation-rule tables are exported complete: `Operation`, `OperationVersion`,
`OperationVersionData`, `OperationNode`, `OperationScope`, `OperationScopeComposition`,
`OperationOutput`, `OperationCodePrefix`, `OperandReference`, `OperandReferenceLocation`,
`Operator`, `OperatorArgument`. They carry the expression text (`OperationVersion.Expression`), its
parsed tree (`OperationNode`, `OperandReference*`), scopes, severities and precondition links, so
that a validation engine can be built on them. The converter copies them; it does not evaluate or
translate the DPM-ML expressions.

**This area is in development.** It changed between 4.3 and 4.4 (`VariableCalculation` removed;
`OperationOutput` and `OperationVersion.OutputVariableID` added), so these tables are documented as
of 4.4 and are not yet considered stable; whether they have settled is checked again in later
releases. (In the 45-table output, the validation-rule tables `vValidationRule*` stay empty.)

## One file with every release

The output holds **every release the Access database contains**, with its history, in one file:
there is no per-release split and no release selection (`--release` does not apply). Versioned
rows keep `StartReleaseID` / `EndReleaseID` as in the source. A consumer selects release `R` with
the validity predicate

```sql
StartReleaseID <= R AND (EndReleaseID IS NULL OR EndReleaseID > R)
```

`ReleaseID` values grow with the publication order of the releases (on 4.4: `1` = 3.4 … `5` = 4.2,
then `1010000003` = 4.2.1 … `1010000124` = 4.4), so the comparison is numeric.

## Type mapping

Each Access column type maps to one SQLite declared type and one value form. The mapping is fixed;
an Access type that is not in this table stops the conversion with an error naming the column,
rather than being written in a guessed form.

| Access type (OLE DB) | Columns in 4.4 (all 70 user tables; exported tables in brackets where it differs) | SQLite type | Value written |
|---|---|---|---|
| Long integer (`adInteger`), Integer (`adSmallInt`), Byte (`adUnsignedTinyInt`), Large number (`adBigInt`) | 243 integer, 1 byte (`OperatorArgument.Order`) | `INTEGER` | the integer |
| Yes/No (`adBoolean`) | 46 (39) | `INTEGER` | `1` for true, `0` for false (Access has no NULL Yes/No value: an unset one is false) |
| Short and long text (`adWChar`) | 132 | `TEXT` | the text as stored, not trimmed |
| Replication ID (`adGUID`) | 51 (50): `RowGUID` ×47, `ConceptGUID` ×4 | `TEXT` | 36 characters, lower case, no braces (`0e40d86d-889c-498e-ae66-46398e615cee`) |
| Decimal (`adNumeric`) | 2 exported (`OperationNode.AbsoluteTolerance`, `RelativeTolerance`); `ChangeLog.Timestamp` is excluded | `TEXT` | the exact decimal in invariant culture (`0.5`, `12`), so no precision is lost (a SQLite `REAL` would round it). All values are `NULL` in 4.4 |
| Date/Time (`adDate`) | 1 exported (`Release.ErrorDate`, all `NULL` in 4.4) | `TEXT` | ISO 8601, `yyyy-MM-ddTHH:mm:ss` |
| `NULL` in any column | | | `NULL` |

Dates that the Access database stores as text (`Release.Date`, `ModuleVersion.FromReferenceDate`,
`ToReferenceDate`, `DocumentVersion.PublicationDate`, `OperationScope.FromSubmissionDate`) are text
columns and are copied as stored.

## Keys, relationships and indexes

- **Names** are those of Access, literally, and are always quoted in the DDL and in the converter's
  own SQL (`"Table"`, `"Release"`, `"Reference"` are SQL keywords).
- **Tables** are created in ordinal order of their names, columns in their Access ordinal order.
- **`NOT NULL`** where the Access column is required.
- **Primary keys** as declared in Access (on 4.4 every exported table has one; 18 are composite,
  for example `ContextComposition (ContextID, PropertyID)` and `TableVersionCell (TableVID, CellID)`).
- **Foreign keys** from the Access relationships, with their rules (`ON UPDATE CASCADE`, and
  `ON DELETE CASCADE` where Access declares it). On 4.4 the user tables have 178 relationships, all on one column (the OLE DB schema lists 179:
  the extra one links two Access system tables). A relationship whose table on either side is excluded is not declared:
  on 4.4, 170 are declared and 8 are not (the change-log tables and `VariableGeneration`). Foreign keys are
  declared but the file is loaded with enforcement off; their integrity is checked by `--validate`
  (`PRAGMA foreign_key_check`).
- **Indexes**: every non-primary-key index that Access declares on an exported table, with the same
  columns, order and uniqueness, named `"<Table>_<Access index name>"`; and an index on the child
  column of every foreign key that no other index of the table starts with, so that joins along the
  relationships do not scan.
- Column defaults are not reproduced: the file is a read-only image, not a database for data entry.

## Measured on the 4.4 database

(2026-11-25, `DPM2 Database_v 4_4_20261006.accdb`; figures of one publication, not of the format.)

- 59 tables exported, 11 excluded; 5,568,105 rows; 170 foreign keys and 168 indexes (all of them
  Access indexes: Access already indexes every relationship column).
- Largest tables: `ContextComposition` 1,757,180 rows, `Concept` 837,042, `OperandReference`
  623,060, `OperandReferenceLocation` 588,878, `TableVersionCell` 407,898, `VariableVersion`
  299,573.
- Conversion time and file size: see [usage.md](usage.md#native-output).
