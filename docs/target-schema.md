# Target schema

The converter always produces the same **45-table DPM distribution schema**, whatever the source
model. The DDL is embedded in the assembly:

```
src/EbaDpm.Converter.Core/Resources/dpm-distribution-schema.sql
```

`Sqlite/SchemaCreator.cs` executes that file to create an empty database (also available on its
own with `--schema-only`, see [usage.md](usage.md)), and the loaders then fill it. Table and
column names are used literally as the format defines them (`mConcept`, `TaxonomyID`, …); they
are never renamed. The file contains only line comments and `CREATE TABLE` statements, because
`SchemaCreator` strips comments and splits the script on `;`.

The semantics of each column are described in the *EIOPA DPM Database Technical
Documentation*: the EBA layout used here is a strict subset of the EIOPA one. Where the
documentation and the DDL disagree (for example `mTaxonomyTable.IsSimplyReuse`, spelled
differently in the documentation, or `IsTableSource`, which it does not mention), the DDL is
followed.

## Table families

| Prefix | Meaning |
|---|---|
| `m*` | **Metadata**: the dictionary and the table structure — the actual product of the conversion |
| `a*` | **Administrative / auxiliary** tables of the distribution database |
| `d*` | **Instance data**: filed reports and filing indicators |
| `v*` | **Validation rules** |

## Which tables are populated

"yes" = populated, "—" = created empty.

### Skeleton and ownership

| Table | DPM 1.0 | DPM 2.0 | Content |
|---|---|---|---|
| `mOwner` | yes | yes | Owners of concepts (EBA, plus two fixed identities of the format) |
| `mOwnerParent` | yes | yes | Owner hierarchy (one row: EBA under Eurofiling) |
| `mLanguage` | yes | yes | Languages (English only) |
| `mRelease` | yes | yes | DPM releases |
| `mReportingFramework` | yes | yes | Frameworks of the exported taxonomies (COREP, FINREP, …) |
| `mTaxonomy` | yes | yes | Exported taxonomies |
| `mTaxonomyPackage` | — | yes | XBRL taxonomy package descriptor; DPM 1.0 has no source for it |
| `mRewriteURI` | — | yes | URI rewrites of the taxonomy package |
| `mConcept` | yes | yes | One concept per emitted object (`Domain`, `Member`, `Table`, `Axis`, …) |
| `mConceptTranslation` | yes | yes | Labels (and DPM 1.0 descriptions) of the concepts |

### Dictionary

| Table | DPM 1.0 | DPM 2.0 | Content |
|---|---|---|---|
| `mDomain` | yes | yes | Domains, including the metric domain `MET` and the `Open` sentinel |
| `mDomainUnion` | — | yes | Union domains and their components; DPM 1.0 does not record the relation |
| `mMember` | yes | yes | Domain members, including metrics as members of `MET` |
| `mDimension` | yes | yes | Dimensions, including the metric dimension `MET` |
| `mMetric` | yes | yes | Metrics: data type, flow type, referenced domain and hierarchy |
| `mHierarchy` | yes | yes | Hierarchies (sub-domains) |
| `mHierarchyNode` | yes | yes | Hierarchy trees |

### Structure

| Table | DPM 1.0 | DPM 2.0 | Content |
|---|---|---|---|
| `mTemplateOrTable` | yes | yes | Tree of table groups, templates and business tables |
| `mTable` | yes | yes | Tables |
| `mTaxonomyTable` | yes | yes | Which tables belong to which taxonomy |
| `mAxis` | yes | yes | Axes (open and closed) |
| `mTableAxis` | yes | yes | Axes of each table, with their order |
| `mAxisOrdinate` | yes | yes | Rows, columns and sheets (ordinates) |
| `mOrdinateCategorisation` | yes | yes | Dimension/member pairs of each ordinate, with their signatures |
| `mOpenAxisValueRestriction` | yes | yes | Hierarchy restricting the values of an open axis |
| `mTableCell` | yes | yes | Cells, with business code and data point signatures |
| `mCellPosition` | yes | yes | Ordinates of each cell |

### Modules

| Table | DPM 1.0 | DPM 2.0 | Content |
|---|---|---|---|
| `mModule` | yes | yes | Modules (entry points / return types) |
| `mConceptualModule` | yes | yes | Mirror of `mModule` |
| `mModuleBusinessTemplate` | yes | yes | Template groups of each module |

### Created empty in both models

| Table(s) | Why empty |
|---|---|
| `vValidationRuleExpressions`, `vValidationRuleTables` | Validation rules are **out of scope**. The DPM 1.0 source has rules in its own format and DPM 2.0 has them as DPM-ML expressions; translating either is a separate project. |
| `dInstance`, `dFilingIndicator` | **Instance tables.** They hold reported data, not metadata; a DPM dictionary has nothing to put there. |
| `aContainerInfo`, `aDDSInfo` | Auxiliary tables of other uses of the format; no DPM source. |
| `mCustomDataType` | No source in either model. |
| `mConceptReference`, `mReference`, `mReferencePart`, `mReferenceValue` | Legal references; not modelled in a usable form by the sources. |
| `mNamespacePrefix`, `mResourceFile`, `mXbrlExportConfiguration` | XBRL packaging details with no DPM source. |

The principle is the same throughout: **the schema contract is kept complete without inventing
data.** A table without a source is created, but stays empty.

### aDatabaseProperties

| DPM 1.0 | DPM 2.0 |
|---|---|
| empty | `Validation syntax version` = `2`, and `Source model` = `DPM 2.0` |

The `Source model` row tells the validator which rule set to apply; its absence means DPM 1.0,
so DPM 1.0 outputs are unaffected by its introduction. See [validation.md](validation.md).

## Conventions in the data

- **Signatures.** `mTableCell.DPS` / `DatapointSignature` and
  `mOrdinateCategorisation.DPS` / `DimensionMemberSignature` follow the grammar in
  [mapping-dpm1.md](mapping-dpm1.md#data-point-signatures). Shaded cells have `NULL` signatures.
- **Sentinels.** ID `9999` is the `Open` member and domain (and, in DPM 2.0, the metric
  dimension). An open axis is categorised with member `9999`.
- **Empty values.** A boolean without a value is `0`; a foreign key without a value is `NULL`.
  `JsonBlob` columns hold an empty BLOB, not `NULL`.
- **IDs are not business keys.** IDs are assigned by the converter and differ from other exports
  of the same release. Compare databases by business key (codes, XBRL codes, business codes,
  signatures), never by ID.
- **Reproducibility.** Two conversions of the same input produce the same content, and the final
  `VACUUM` keeps the file size stable.
