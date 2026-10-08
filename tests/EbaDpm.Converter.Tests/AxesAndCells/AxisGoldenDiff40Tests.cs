using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.AxesAndCells;

/// <summary>
/// Golden-level comparison of axes against <c>EBA_4.0_ERRATA_5.db</c>, the SUBSIDIARY reference
/// (3.2 takes precedence; 4.0 is used where 3.2 does not reach), over the four comparable 4.0
/// taxonomies: <c>COREP 4.0</c>, <c>DORA 4.0</c>, <c>IF 4.0</c>, <c>MICA 4.0</c> (150/151 tables,
/// 368 axes, 4,220 ordinates; <c>corep 3.4</c> is missing because the Access v4.1 does not contain
/// it, which is not a failure).
///
/// Unlike <see cref="AxisGoldenDiff32Tests"/>, CONTAINMENT is demanded here, not equality: 4.0
/// does not arbitrate content except where 3.2 does not reach, and several convention divergences
/// against 4.0 are already known (the <c>mTableAxis</c> class order fails on 22 of 150 tables
/// because of a non-derivable permutation in the reference generator, so that criterion is NOT
/// repeated here; it is already closed against the primary reference in
/// <see cref="AxisGoldenDiff32Tests.TableAxis_ClassSequence_MatchesReference32_ForAllTables"/>).
///
/// Critical acceptance layer except where stated otherwise.
/// </summary>
[Collection("AxesAndCells")]
[Trait("Tier", "RealData")]
public sealed class AxisGoldenDiff40Tests
{
    private static readonly HashSet<string> TargetTaxonomies = new(StringComparer.Ordinal)
    {
        "corep_4.0", "dora_4.0", "if_4.0", "mica_4.0",
    };

    private readonly AxisAndCellFixture _fixture;

    public AxisGoldenDiff40Tests(AxisAndCellFixture fixture)
    {
        _fixture = fixture;
    }

    // ------------------------------------------------------------------
    // Table coverage by business key (taxonomy, TableCode): EVERYTHING that 4.0 has in these four
    // taxonomies must exist in the generated output. 150/151 in the reference because
    // 'corep 3.4' is not in the Access v4.1; the generated output is not required to contain it.
    // ------------------------------------------------------------------

    [DataFact]
    public void TableCoverage_GeneratedContainsReference40_ForTheFourComparableTaxonomies()
    {
        var generated = BusinessKeySupport.BuildTableIdsByBusinessKey(_fixture.GeneratedConnection, TargetTaxonomies).Keys.ToHashSet();
        var reference = BusinessKeySupport.BuildTableIdsByBusinessKey(_fixture.Reference40Connection, TargetTaxonomies).Keys.ToHashSet();

        Assert.True(reference.Count >= 150, $"The 4.0 reference only has {reference.Count} comparable tables; at least 150 were expected.");

        var missing = reference.Except(generated).ToList();
        Assert.True(
            missing.Count == 0,
            $"{missing.Count} tables of the 4.0 reference are not in the generated output: " +
            string.Join(", ", missing.Take(20).Select(k => $"{k.Taxonomy}/{k.TableCode}")));
    }

    // ------------------------------------------------------------------
    // mAxis.AxisCode of OPEN axes: literal, 100% (95/95 open Y+Z axes of 4.0). Open axes are
    // matched by the XBRL code of their dimension, not by AxisCode (it would be circular) nor by
    // AxisLabel (it differs on purpose on key Y axes between generations).
    // ------------------------------------------------------------------

    private Dictionary<(string Taxonomy, string TableCode, string DimensionXbrlCode), string?> OpenAxisCodesByDimension(
        Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var rows = QueryHelpers.Rows(
            connection,
            """
            SELECT t."TaxonomyCode", tab."TableCode", dim."DimensionXBRLCode", a."AxisCode"
            FROM "mAxis" a
            JOIN "mTableAxis" ta     ON ta."AxisID" = a."AxisID"
            JOIN "mTable" tab        ON tab."TableID" = ta."TableID"
            JOIN "mTaxonomyTable" tt ON tt."TableID" = tab."TableID"
            JOIN "mTaxonomy" t       ON t."TaxonomyID" = tt."TaxonomyID"
            JOIN "mAxisOrdinate" o   ON o."AxisID" = a."AxisID"
            JOIN "mOrdinateCategorisation" oc ON oc."OrdinateID" = o."OrdinateID" AND oc."MemberID" = 9999
            JOIN "mDimension" dim    ON dim."DimensionID" = oc."DimensionID"
            WHERE a."IsOpenAxis" = 1
            """,
            4);

        var result = new Dictionary<(string, string, string), string?>();
        foreach (var row in rows)
        {
            var taxonomy = BusinessKeySupport.NormalizeTaxonomyCode(row[0]!);
            if (!TargetTaxonomies.Contains(taxonomy))
            {
                continue;
            }

            result[(taxonomy, row[1]!, BusinessKeySupport.LocalXbrlPart(row[2]!))] = row[3];
        }

        return result;
    }

    [DataFact]
    public void AxisCode_OfOpenAxes_MatchesReference40_Literally()
    {
        var generated = OpenAxisCodesByDimension(_fixture.GeneratedConnection);
        var reference = OpenAxisCodesByDimension(_fixture.Reference40Connection);

        Assert.True(reference.Count > 0);

        var failures = new List<string>();
        foreach (var (key, refCode) in reference)
        {
            if (!generated.TryGetValue(key, out var genCode))
            {
                failures.Add($"{key.Taxonomy}/{key.TableCode}/{key.DimensionXbrlCode}: open axis missing from the generated output");
                continue;
            }

            if (!string.Equals(genCode, refCode, StringComparison.Ordinal))
            {
                failures.Add($"{key.Taxonomy}/{key.TableCode}/{key.DimensionXbrlCode}: generated='{genCode}' reference='{refCode}'");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count}/{reference.Count} open-axis AxisCode values different from the 4.0 reference:\n" + string.Join("\n", failures.Take(20)));
    }

    // ------------------------------------------------------------------
    // mOpenAxisValueRestriction: presence and HierarchyID (case and REL normalised) by
    // CONTAINMENT against 4.0 (95/95 presence, 45/45 HierarchyID after normalisation).
    // HierarchyStartingMemberID is NOT compared: 4.0 NEVER populates it (0/45); that is a loss in
    // the reference generator, not a rival convention. Comparing it would be a relaxation
    // disguised as an assertion, so it is explicitly omitted.
    // ------------------------------------------------------------------

    private static string NormalizeHierarchyCode(string code)
    {
        var upper = code.Trim().ToUpperInvariant();
        var relIndex = upper.IndexOf("_REL_", StringComparison.Ordinal);
        return relIndex >= 0 ? upper[..relIndex] : upper;
    }

    private Dictionary<(string Taxonomy, string TableCode, string DimensionXbrlCode), string> RestrictionHierarchyByDimension(
        Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var rows = QueryHelpers.Rows(
            connection,
            """
            SELECT t."TaxonomyCode", tab."TableCode", dim."DimensionXBRLCode", h."HierarchyCode"
            FROM "mOpenAxisValueRestriction" r
            JOIN "mAxis" a           ON a."AxisID" = r."AxisID"
            JOIN "mTableAxis" ta     ON ta."AxisID" = a."AxisID"
            JOIN "mTable" tab        ON tab."TableID" = ta."TableID"
            JOIN "mTaxonomyTable" tt ON tt."TableID" = tab."TableID"
            JOIN "mTaxonomy" t       ON t."TaxonomyID" = tt."TaxonomyID"
            JOIN "mHierarchy" h      ON h."HierarchyID" = r."HierarchyID"
            JOIN "mAxisOrdinate" o   ON o."AxisID" = a."AxisID"
            JOIN "mOrdinateCategorisation" oc ON oc."OrdinateID" = o."OrdinateID" AND oc."MemberID" = 9999
            JOIN "mDimension" dim    ON dim."DimensionID" = oc."DimensionID"
            """,
            4);

        var result = new Dictionary<(string, string, string), string>();
        foreach (var row in rows)
        {
            var taxonomy = BusinessKeySupport.NormalizeTaxonomyCode(row[0]!);
            if (!TargetTaxonomies.Contains(taxonomy))
            {
                continue;
            }

            result[(taxonomy, row[1]!, BusinessKeySupport.LocalXbrlPart(row[2]!))] = row[3]!;
        }

        return result;
    }

    [DataFact]
    public void OpenAxisValueRestriction_PresenceAndHierarchyId_MatchReference40()
    {
        var generated = RestrictionHierarchyByDimension(_fixture.GeneratedConnection);
        var reference = RestrictionHierarchyByDimension(_fixture.Reference40Connection);

        Assert.True(reference.Count > 0);

        var missing = new List<string>();
        var hierarchyFailures = new List<string>();

        foreach (var (key, refHierarchy) in reference)
        {
            if (!generated.TryGetValue(key, out var genHierarchy))
            {
                missing.Add($"{key.Taxonomy}/{key.TableCode}/{key.DimensionXbrlCode}");
                continue;
            }

            if (!string.Equals(NormalizeHierarchyCode(genHierarchy), NormalizeHierarchyCode(refHierarchy), StringComparison.Ordinal))
            {
                hierarchyFailures.Add($"{key.Taxonomy}/{key.TableCode}/{key.DimensionXbrlCode}: generated='{genHierarchy}' reference='{refHierarchy}'");
            }
        }

        Assert.True(missing.Count == 0, $"{missing.Count}/{reference.Count} restrictions of the 4.0 reference missing from the generated output:\n" + string.Join("\n", missing.Take(20)));
        Assert.True(hierarchyFailures.Count == 0, $"{hierarchyFailures.Count}/{reference.Count} different HierarchyID values (normalised):\n" + string.Join("\n", hierarchyFailures.Take(20)));
    }

    // ------------------------------------------------------------------
    // mTableCell.IsShaded: literal against 4.0, with TEN known divergences named one by one by
    // the BusinessCode of the REFERENCE. They are not a defect of the reference nor a
    // non-derivable value: they are release drift. The source is v4.1 and this reference is 4.0
    // ERRATA 5; the most plausible explanation is that the EBA shaded these ten cells AFTER
    // publishing 4.0. The 129,549 cells of 3.2 and the 1,310 of 4.2 are compared literally and
    // without exception in other tests; nothing else is relaxed here: the COREP_4.0 taxonomy is
    // not excluded, no percentage threshold is used, and the exception is not widened to "the
    // cells of C_09.04". BOTH directions are checked: the ten STILL diverge (if one stopped
    // diverging, the exception has expired) and there is NO eleventh (the general criterion is
    // never relaxed).
    // ------------------------------------------------------------------

    private static readonly HashSet<string> DeclaredIsShadedDivergences = new(StringComparer.Ordinal)
    {
        "{C_07.00.d,0300,0230,0010}",
        "{C_07.00.d,0320,0230,0010}",
        "{C_09.04,0110,0030,0010}",
        "{C_09.04,0120,0030,0010}",
        "{C_09.04,0130,0030,0010}",
        "{C_09.04,0140,0030,0010}",
        "{C_09.04,0150,0020,0010}",
        "{C_09.04,0160,0020,0010}",
        "{C_02.00.b,0035,0020}",
        "{C_02.00.b,0036,0020}",
    };

    [DataFact]
    public void IsShaded_MatchesReference40_Literally_ExceptTheTenDeclaredDD6ToDD15()
    {
        // Cells are NOT matched by BusinessCode: its open-axis convention differs on purpose
        // between the output (which follows 3.2) and 4.0 (which expands the coordinate), so the
        // code TEXT differs even when the cell is the same. They are matched by the set of
        // positions over CLOSED axes, which is comparable. The BusinessCode of the REFERENCE
        // (used to name the ten known divergences) is read separately, only to name the object,
        // never to match.
        var tableIdsGenerated = BusinessKeySupport.BuildTableIdsByBusinessKey(_fixture.GeneratedConnection, TargetTaxonomies);
        var tableIdsReference = BusinessKeySupport.BuildTableIdsByBusinessKey(_fixture.Reference40Connection, TargetTaxonomies);

        var generatedIndex = BusinessKeySupport.BuildClosedCellPositionIndex(_fixture.GeneratedConnection, tableIdsGenerated.Values.ToHashSet());
        var referenceIndex = BusinessKeySupport.BuildClosedCellPositionIndex(_fixture.Reference40Connection, tableIdsReference.Values.ToHashSet());

        var generatedShaded = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"CellID\", \"IsShaded\" FROM \"mTableCell\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]);
        var referenceShaded = QueryHelpers.Rows(_fixture.Reference40Connection, "SELECT \"CellID\", \"IsShaded\" FROM \"mTableCell\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]);
        var referenceBusinessCode = QueryHelpers.Rows(_fixture.Reference40Connection, "SELECT \"CellID\", \"BusinessCode\" FROM \"mTableCell\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]!);

        var compared = 0;
        var missing = new List<string>();
        var unexpectedMismatches = new List<string>(); // diverge and is NOT one of the ten declared: a real failure
        var foundDeclaredDivergences = new HashSet<string>(StringComparer.Ordinal); // which of the 10 have been seen to diverge

        foreach (var (tableKey, refTableId) in tableIdsReference)
        {
            if (!tableIdsGenerated.TryGetValue(tableKey, out var genTableId))
            {
                continue; // checked elsewhere (table coverage)
            }

            foreach (var ((tableId, positionKey), refCellId) in referenceIndex)
            {
                if (tableId != refTableId)
                {
                    continue;
                }

                if (!generatedIndex.TryGetValue((genTableId, positionKey), out var genCellId))
                {
                    missing.Add($"{tableKey.Taxonomy}/{tableKey.TableCode}: cell missing from the generated output ({positionKey})");
                    continue;
                }

                compared++;
                var refShaded = referenceShaded.GetValueOrDefault(refCellId);
                var genShaded = generatedShaded.GetValueOrDefault(genCellId);

                if (genShaded == refShaded)
                {
                    continue;
                }

                var refBusinessCode = referenceBusinessCode.GetValueOrDefault(refCellId, "?");
                if (DeclaredIsShadedDivergences.Contains(refBusinessCode))
                {
                    foundDeclaredDivergences.Add(refBusinessCode);
                }
                else
                {
                    unexpectedMismatches.Add(
                        $"{tableKey.Taxonomy}/{tableKey.TableCode}/{positionKey} (reference BusinessCode='{refBusinessCode}'): " +
                        $"generated={genShaded} reference={refShaded} - NOT one of the ten declared divergences");
                }
            }
        }

        var missingDeclaredDivergences = DeclaredIsShadedDivergences.Except(foundDeclaredDivergences).ToList();

        Assert.True(compared > 50000, $"Only {compared} cells were compared: position matching failed wholesale.");
        Assert.True(missing.Count == 0, $"{missing.Count}/{compared} cells of the 4.0 reference missing from the generated output:\n" + string.Join("\n", missing.Take(20)));

        Assert.True(
            unexpectedMismatches.Count == 0,
            $"{unexpectedMismatches.Count} cells diverge on IsShaded without being one of the ten declared divergences (an undeclared eleventh object):\n" +
            string.Join("\n", unexpectedMismatches.Take(20)));

        Assert.True(
            missingDeclaredDivergences.Count == 0,
            $"{missingDeclaredDivergences.Count} of the ten declared divergences NO LONGER diverge: " +
            "the exception has expired and must be removed from the list of known divergences:\n" +
            string.Join("\n", missingDeclaredDivergences));

        Assert.Equal(10, foundDeclaredDivergences.Count);
    }
}
