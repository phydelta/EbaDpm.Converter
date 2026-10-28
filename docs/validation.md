# Validation

`--validate` checks a database produced by the converter. It is the same machinery the test
suite uses, exposed as a command. See [usage.md](usage.md#validate) for the command syntax.

## Sources of truth and order of authority

Three artefacts describe the same DPM, and they do not carry the same weight:

| Artefact | Published by | Role |
|---|---|---|
| The DPM **Access database** | EBA | **Source of truth.** The input of the conversion |
| The **Annotated Table Layouts** (`.xlsx`) | EBA | **Source of truth.** An independent description of every table and of the dimensions and members of every cell |
| A **reference SQLite export** of the DPM in the distribution schema | A third party | **Instrument, not a specification.** Another implementation of the same transformation, derived from the published taxonomies |

The order of authority is **Access → layouts → reference export**. A difference against a
reference export is information: it is not a defect of the converter until the Access database or
a layout confirms it. When the Access database and the reference disagree, the converter follows
the Access database.

The goal of the tool is to produce the SQLite edition before any reference export of that release
exists, so the planes that need no reference (A and C) are the ones that decide whether a
conversion is valid.

## The three planes

| Plane | Input | Compares against | Decides the exit code |
|---|---|---|---|
| **A** | none | Internal invariants of the output | **Yes** |
| **B** | `--reference` | A reference SQLite export, by business key | **No** — reported and counted separately |
| **C** | `--layouts` | The EBA Annotated Table Layouts, signature by signature | **Yes** |

Every check belongs to one plane and one layer:

- **Critical** — zero tolerance. A failure in plane A or C makes the run exit with code `3`.
- **Informative** — reported in the console and the JSON report; never fails the run.

Plane B checks also carry a layer, and a critical plane B failure is shown with status `fail`,
but plane B never contributes to the exit code. Its failures are counted on a separate line so
they are never hidden inside the totals.

Validation never modifies the database being validated: it copies it to a temporary file and
creates its auxiliary indexes there.

The validator reads the `Source model` property of `aDatabaseProperties` to know whether the
output came from a DPM 1.0 or a DPM 2.0 source (DPM 2.0 outputs carry `Source model = DPM 2.0`;
its absence means DPM 1.0) and adapts the scope of the checks whose expectations differ between
the two models.

## Plane A — internal invariants

Plane A needs nothing but the generated database and always runs. Its check codes start with `A-`.

| Family | What it verifies |
|---|---|
| `A-INT` | Referential integrity: no orphan rows in any foreign key of the schema (dictionary, templates and tables, modules, cell positions, categorisations); no unused `mConcept`; no dangling or zero-valued optional references |
| `A-UNQ` | Uniqueness of business keys: `DimensionXBRLCode`, `(DomainID, MemberCode)`, `(DomainID, HierarchyCode)`, `BusinessCode` within a table, `(AxisID, OrdinateCode)`, `(OrdinateID, DimensionID)`, template codes per taxonomy and level, module codes per taxonomy |
| `A-DIC` | Dictionary rules: typed domains have no default member, at most one default per domain, `IsTypedDimension` agrees with the domain, default members belong to the dimension's domain, exactly one metric dimension `MET`, every metric has its member in the `MET` domain, XBRL code shapes |
| `A-HIE` | Hierarchy trees: no cycles, `Level` consistent with the parent, parent in the same hierarchy, `Path` well formed, unique siblings and order, nodes inside the hierarchy's domain |
| `A-SEN` | Open-axis sentinel conventions of the distribution schema (domain and member `9999` "Open"), and absence of the source's own sentinels |
| `A-TPL` | Template and table tree: node types and levels, parents, taxonomy consistency, table links, frameworks used by at least one taxonomy |
| `A-AXE` | Axes and ordinates: one closed X axis per table and at most one closed Y axis, open-axis ordinate shape, `Order` reproducible in pre-order from the tree, fixed-value columns |
| `A-CEL` | Cells and positions: the `BusinessCode` rebuilt from `mCellPosition` equals the emitted one byte for byte, every cell has at least two positions on axes of its own table, abstract headers imply shaded cells, a dimension belongs to exactly one axis of a table, a cell never receives two members of the same dimension |
| `A-OCA` | Ordinate categorisation: the effective (inherited) categorisation of every closed, non-abstract ordinate is consistent with its own set; members belong to the dimension's domain; open axes are categorised with the sentinel |
| `A-OAR` | Open-axis value restrictions: column conventions of `mOpenAxisValueRestriction` |
| `A-OMR` | Per-ordinate metric restrictions (`mOrdinateMetricRestriction`): the ordinate carries exactly one `MET` categorisation, that metric is enumerated (`Enumeration/Code` in DPM 2.0, `Code` in DPM 1.0), the hierarchy belongs to the metric's `ReferencedDomainID`, and a starting member, when present, is a node of the hierarchy (with `IsStartingMemberIncluded` set exactly when it is) |
| `A-MOD` | Modules: template links point to first-level table groups of the same taxonomy, one conceptual module per module, `XBRLSchemaRef` shape, dense ordering, one-to-one taxonomy keys; for DPM 2.0 (`A-MOD-10`), every taxonomy has at least one module and each module's `XBRLSchemaRef` framework and release equal its taxonomy's |
| `A-CPT` | Concepts: `ConceptType` belongs to the target vocabulary, concept counts per type match their entities, every concept has a label translation, `ReleaseID` only where the schema expects it |
| `A-VAC` | Tables that must be empty are empty (for example the validation-rule tables, references and instance tables), and the tables populated with fixed rows have exactly those rows |

An output produced by a converter version without the extension table `mOrdinateMetricRestriction`
is still validated: the `A-OMR` checks and the orphan checks on that table are reported as skipped,
naming the reason, and `A-VAC-03` fails because the schema is not the expected one. Plane C does the
same with `C-MHR-01` and `C-MHR-02`, and also with a layout repository built before the metric
bracket was stored.

## Plane B — comparison against a reference export (informational)

Plane B runs with `--reference <file>`. It compares only the taxonomies present in both
databases, always by business key — codes, XBRL codes, ordinate paths, signatures — and never by
surrogate ID, because IDs are not stable between independent exports. Most checks test
**containment**: the generated sets must contain the reference sets.

| Family | What it compares |
|---|---|
| `B-SCH` | Schema identical to the 4.2 reference: tables, columns, names and types (only when the reference is a 4.2 export) |
| `B-DIC` | Dictionary: domains, members, dimensions, hierarchies and metrics by code and XBRL code |
| `B-LAB` | Labels and descriptions (informative) |
| `B-TPL` | Table codes per taxonomy |
| `B-AXE` | Ordinate codes by closed ordinate path, axis orientation and openness, open-axis codes, axis and ordinate labels |
| `B-CEL` | The position set of every cell, and `IsShaded` |
| `B-OCA` | Ordinate categorisation pairs |
| `B-MOD` | Modules and their `XBRLSchemaRef` |

The reference file name selects its role: a name containing `3.2`, `4.0` or `4.2` selects the
comparison scope and the known exceptions recorded for that release.

Plane B is informational because the reference export is an independent implementation built
with its own deductions. It remains valuable for detecting regressions, but it cannot decide
whether a conversion is correct.

## Plane C — comparison against the EBA Annotated Table Layouts

Plane C runs with `--layouts <file>`, a layout repository built by `--extract-layouts` (see
[table-layouts.md](table-layouts.md)). Each table of the output is matched with the layout sheet
of the same table code and release, and each layout cell is compared with the corresponding cell
of the output.

**Release pairing.** The release of an output table is the release of its taxonomy
(`TaxonomyCode` = `{framework} {release}`). A table code can belong to taxonomies of two releases
of the same framework (for example `finrep 4.2` and `finrep 4.2.1`, when modules of both
publications use it). For each layout sheet, the tables whose release equals the sheet's release
are used. Only when there is none does the point-release tolerance apply: a layout for `4.2.1` is
matched to a `4.2` table, and the match is reported in `C-REL-01` or `C-EQU-03`. Each sheet is
compared only with the tables of its own publication. When a code has sheets of two releases, a
layout signature counts as contained only if it is contained in every one of them, and a
violation is reported once, naming the sheet releases where it failed.

| Check | Layer | What it verifies |
|---|---|---|
| `C-DPS-01` | Critical | Every datapoint signature drawn in the layout is **contained** in one of the output's signatures, and no dimension the layout mentions carries a different member. Violations listed in the known-divergences file are excluded |
| `C-DPS-02` | Informative | Terms the output emits and the layout does not mention. Layouts legitimately omit some (abstract rows, open-axis keys, table-wide context), so this is never critical |
| `C-EQU-01` | Critical | Every group of cells declared equal by an `==` rule in the layout's cell comments carries the same datapoint signature in the output. This check crosses table boundaries |
| `C-EQU-02` | Informative | Coverage of `C-EQU-01`: total rules, checkable rules, and why the others are not checkable |
| `C-EQU-03` | Informative | Layout sheets matched to an equality rule only through the point-release tolerance |
| `C-COB-01` | Informative | Coverage: tables and signatures compared, and what was excluded and why. Always reported, even when nothing fails |
| `C-CLS-01` | Informative | Breakdown of compared tables by class (regular, open axis, true Z axis) |
| `C-REL-01` | Informative | Layout sheets matched only through the point-release tolerance (a layout for release `4.2.1` matched to a `4.2` table) |
| `C-MHR-01` | Critical | Every metric annotated in the layout with a hierarchy (`(qAYV) …[qCO:new_CO5]`) has, on the matching ordinate of the output (same table, axis and ordinate code, same metric), the same hierarchy and domain: the per-ordinate restriction in `mOrdinateMetricRestriction`, or else `mMetric.ReferencedHierarchyID`. Violations listed in the known-divergences file are excluded |
| `C-MHR-02` | Informative | Coverage of `C-MHR-01`: annotated metrics compared, matched without any hierarchy in the output (for example a metric on an open axis), not matched, and sheet-level annotations not examined |
| `C-KEY-01` | Informative | Layout signatures excluded because they describe the key column of an open table: the layout draws it as a grid column, while in the distribution schema it is the open axis itself |

Plane C reports its coverage even when everything passes, so that "zero violations" is always
accompanied by how much was actually examined. A check that examined nothing is reported as
skipped, not as passed.

**Limit.** Datapoint signatures omit default members, which are a large share of
`mOrdinateCategorisation` rows. Plane C therefore says nothing about those rows; they are covered
by plane A and by the mapping rules themselves.

## Known exceptions

An exception never relaxes a check. Thresholds, criteria and scope stay at maximum strictness; the
only thing an exception can do is exclude **one named object, identified by its exact business
key**, from one specific check, with a written reason. Exceptions are never applied by pattern,
category, table or prefix.

Each exception has a kind:

| Kind | Meaning |
|---|---|
| Reference defect | The reference export is wrong for this object, with positive evidence from the Access database or a layout |
| Declared divergence | The output differs from the reference on purpose (the difference is not derivable from the source, or depends on the release) |
| Origin anomaly | An anomaly in the source Access database itself, checked in plane A without any reference |

An exception that no longer matches any object is **stale**. In planes A and C a stale exception
is a critical failure, so the exception list cannot silently outlive the problem it described.
Exceptions whose check did not run in the session are reported as *not evaluated*, which is
distinct from "evaluated and found nothing". The console groups exceptions by identifier and names
every stale one individually; the JSON report lists all of them object by object.

### Plane C known divergences

The layout comparison has its own list of known divergences, kept as data rather than code:
[`plane-c-known-divergences-4.2.tsv`](../src/EbaDpm.Converter.Core/Resources/plane-c-known-divergences-4.2.tsv),
embedded in the assembly. Each line names a table code, the divergence type (`contradicts`: the
layout gives a dimension another member; `no-match`: no output signature contains the layout
signature) and the exact layout signature. A `C-MHR-01` divergence has its own type, `hierarchy`,
and names one annotated metric by `{release}|{axis}|{ordinate code}|{metric code}`, with a
mandatory reason. The entries are cases arbitrated against the Access
database in which the **layout** is wrong: for example, a sheet draws ordinates that the Access
database does not assign to that table.

The file is measured against one specific release, declared in its `CorpusRelease` header. It is
applied only to a DPM 2.0 output whose cutoff release is that release; against any other release
the list is reported as not evaluated rather than stale. The file is **regenerated for each new
release**: a new release gets its own file, and no divergence is hard-coded in C#.

## Validation status at version 1.1.0

Plane A exits with code 0 on the outputs of the three supported sources (DPM 1.0 release 4.1,
DPM 2.0 releases 4.2 and 4.3). Plane C covers both source models:

| Source | Layouts | Tables | Signatures | Violations |
|---|---|---|---|---|
| DPM 1.0 | 3.2 | 472 | 86,115 | 0 |
| DPM 1.0 | 4.0 | 140 | 22,192 | 0 |
| DPM 2.0, release 4.2 | 4.2 | 787 | 90,972 | 0 (99.95 % match; the remainder is covered by the known layout divergences and the open-table key-column exclusion) |

On an earlier DPM 1.0 output with a known categorisation defect, the same comparison reports
nine violations — the plane detects real losses rather than passing by construction.

## Fidelity against the 4.2 reference export

Comparing the output of the DPM 2.0 4.2 Access database with a 4.2 reference export, table by
table: all 45 tables are present on both sides, **25 are identical**, and the remaining 20 differ
for documented reasons. Counts are row counts.

| Table(s) | Output | Reference | Difference | Reason |
|---|---|---|---|---|
| `mOrdinateCategorisation` | 196,665 | 71,148 | +125,517 | The output emits the full effective categorisation of each ordinate (including pairs inherited down the ordinate tree). The EBA layouts support this form; the reference uses a reduced form |
| `mConcept` · `mConceptTranslation` | 57,077 · 57,070 | 79,509 · 79,572 | −22,432 · −22,502 | Derived from the rows below: no validation rules, no duplicated hierarchies and hierarchy nodes, and a few descriptions the Access database does not contain |
| `mHierarchyNode` · `mHierarchy` | 16,538 · 1,154 | 31,004 · 1,572 | −14,466 · −418 | The reference duplicates hierarchies per release as transition scaffolding that does not come from the source; most duplicates carry the same tree. Residual difference: 22 nodes (0.07 %) |
| `vValidationRuleTables` · `vValidationRuleExpressions` | 0 · 0 | 14,634 · 7,378 | −14,634 · −7,378 | Validation rules are out of scope; the tables are created empty |
| `mMetric` · `mMember` | 2,107 · 12,949 | 2,407 · 13,130 | −300 · −181 | Release-suffixed duplicates are not reproduced; for renamed codes the output emits the current code where the reference emits the retired one |
| `mTable` · `mTaxonomyTable` · `mTemplateOrTable` | 847 · 847 · 1,530 | 846 · 846 · 1,529 | +1 each | One table the Access database declares and the reference does not (`pay 4.1` / `S_04.00`) |
| `mAxis` · `mTableAxis` · `mAxisOrdinate` | 1,993 · 1,993 · 20,712 | 1,991 · 1,991 · 20,705 | +2 · +2 · +7 | Follows from the previous row |
| `mTableCell` · `mCellPosition` | 163,218 · 415,586 | 163,206 · 415,562 | +12 · +24 | Follows from the previous row |
| `mDimension` | 1,100 | 1,101 | −1 | `TNS`, a dimension that nothing in the Access database or in the reference itself uses (known exception DD-26) |
| `mTaxonomy` | 32 | 31 | +1 | `pay 4.1`, declared by the Access database |
| `aDatabaseProperties` | 2 | 1 | +1 | The output adds the `Source model` property |

Row counts are a weak measure of fidelity: two opposite differences can cancel out. Up to version
1.0.0 the output had one table more and one table fewer than the reference, and `mTable` matched
846 = 846; fixing the missing one in 1.1.0 made the count differ by one. The business-key
comparisons of planes B and C are the meaningful measure.

Some descriptions present in the reference (legal citations attached to hierarchy nodes) are not
declared anywhere in the Access database. They are deliberately not emitted: the converter exports
what the source declares, not what another export contains.
