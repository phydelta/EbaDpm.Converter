using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.AxesAndCells;

/// <summary>
/// Golden-level comparison of axes against <c>EBA_3.2_phase_1.db</c>, the PRIMARY content
/// reference, over the FOUR taxonomies of <see cref="AxisAndCellFixture"/> that cover 3.2 fully:
/// <c>AE 3.2</c>, <c>COREP 3.2</c>, <c>GSII 3.2</c>, <c>IF 3.2</c> (217 tables, 522 axes, 7,615
/// ordinates, 129,549 cells, 358,946 positions, 58 restrictions).
///
/// Because the converted universe is EXACTLY the 3.2 universe, these tests demand EQUALITY, not
/// mere containment: a shortfall here is a real failure, not a taxonomy-coverage question.
///
/// Critical acceptance layer.
/// </summary>
[Collection("AxesAndCells")]
[Trait("Tier", "RealData")]
public sealed class AxisGoldenDiff32Tests
{
    private static readonly HashSet<string> TargetTaxonomies = new(StringComparer.Ordinal)
    {
        "ae_3.2", "corep_3.2", "gsii_3.2", "if_3.2",
    };

    private readonly AxisAndCellFixture _fixture;

    public AxisGoldenDiff32Tests(AxisAndCellFixture fixture)
    {
        _fixture = fixture;
    }

    // ------------------------------------------------------------------
    // Exact census: 522 / 7,615 / 129,549 / 358,946 / 58, ON BOTH SIDES. Double guard: if the
    // reference changes, it shows here before in any other test.
    // ------------------------------------------------------------------

    // <see cref="AxisAndCellFixture"/> converts EIGHT taxonomies (the four of 3.2 PLUS the four of
    // the subsidiary 4.0), so the generated side must be RESTRICTED to the four 3.2 ones before
    // comparing; the 3.2 reference already contains only those four, so it needs no filtering.

    private const string GeneratedAxisJoin =
        """
        FROM "mAxis" a
        JOIN "mTableAxis" ta    ON ta."AxisID"   = a."AxisID"
        JOIN "mTable" tab       ON tab."TableID" = ta."TableID"
        JOIN "mTaxonomyTable" tt ON tt."TableID" = tab."TableID"
        JOIN "mTaxonomy" t      ON t."TaxonomyID" = tt."TaxonomyID"
        """;

    /// <summary>
    /// Sums, TAXONOMY BY TAXONOMY, the DISTINCT count of the given identifier; NEVER a global
    /// distinct. This is deliberate: a few tables are REUSED between <c>COREP_3.2</c> and
    /// <c>IF_3.2</c> (e.g. <c>C_18.00</c>, <c>C_21.00</c>...) via <c>mTaxonomyTable</c> (N:M, table
    /// reuse across taxonomies). Our output models them as ONE physical row referenced by two
    /// taxonomies (correct according to the schema); the 3.2 reference, in contrast, does not
    /// physically share those rows between taxonomies (no overlaps). A GLOBAL distinct on the
    /// generated side falls below the reference figure (112,665 instead of 129,549) without being
    /// a conversion failure; it is the correct consequence of deduplicating. The comparable figure
    /// is the SUM per taxonomy.
    /// </summary>
    private long GeneratedFilteredCount(string selectDistinct, string join, string taxonomyColumnExpr)
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            $"SELECT DISTINCT {selectDistinct}, {taxonomyColumnExpr} {join}",
            2);
        return rows
            .Where(r => TargetTaxonomies.Contains(BusinessKeySupport.NormalizeTaxonomyCode(r[1]!)))
            .GroupBy(r => BusinessKeySupport.NormalizeTaxonomyCode(r[1]!))
            .Sum(g => g.Select(r => r[0]).Distinct().Count());
    }

    [DataFact]
    public void Census_Axes_Is522_OnBothSides()
    {
        var reference = QueryHelpers.Scalar(_fixture.Reference32Connection, "SELECT COUNT(*) FROM \"mAxis\"");
        Assert.Equal(522, reference);

        var generated = GeneratedFilteredCount("a.\"AxisID\"", GeneratedAxisJoin, "t.\"TaxonomyCode\"");
        Assert.Equal(522, generated);
    }

    [DataFact]
    public void Census_Ordinates_Is7615_OnBothSides()
    {
        var reference = QueryHelpers.Scalar(_fixture.Reference32Connection, "SELECT COUNT(*) FROM \"mAxisOrdinate\"");
        Assert.Equal(7615, reference);

        var join = GeneratedAxisJoin + " JOIN \"mAxisOrdinate\" o ON o.\"AxisID\" = a.\"AxisID\"";
        var generated = GeneratedFilteredCount("o.\"OrdinateID\"", join, "t.\"TaxonomyCode\"");
        Assert.Equal(7615, generated);
    }

    [DataFact]
    public void Census_Cells_Is129549_OnBothSides()
    {
        var reference = QueryHelpers.Scalar(_fixture.Reference32Connection, "SELECT COUNT(*) FROM \"mTableCell\"");
        Assert.Equal(129549, reference);

        var join =
            """
            FROM "mTableCell" c
            JOIN "mTable" tab       ON tab."TableID" = c."TableID"
            JOIN "mTaxonomyTable" tt ON tt."TableID" = tab."TableID"
            JOIN "mTaxonomy" t      ON t."TaxonomyID" = tt."TaxonomyID"
            """;
        var generated = GeneratedFilteredCount("c.\"CellID\"", join, "t.\"TaxonomyCode\"");
        Assert.Equal(129549, generated);
    }

    [DataFact]
    public void Census_Positions_Is358946_OnBothSides()
    {
        var reference = QueryHelpers.Scalar(_fixture.Reference32Connection, "SELECT COUNT(*) FROM \"mCellPosition\"");
        Assert.Equal(358946, reference);

        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT cp."CellID", cp."OrdinateID", t."TaxonomyCode"
            FROM "mCellPosition" cp
            JOIN "mTableCell" c      ON c."CellID" = cp."CellID"
            JOIN "mTable" tab        ON tab."TableID" = c."TableID"
            JOIN "mTaxonomyTable" tt ON tt."TableID" = tab."TableID"
            JOIN "mTaxonomy" t       ON t."TaxonomyID" = tt."TaxonomyID"
            """,
            3);
        var generated = rows.Count(r => TargetTaxonomies.Contains(BusinessKeySupport.NormalizeTaxonomyCode(r[2]!)));
        Assert.Equal(358946, generated);
    }

    [DataFact]
    public void Census_OpenAxisValueRestrictions_Is58_OnBothSides()
    {
        var reference = QueryHelpers.Scalar(_fixture.Reference32Connection, "SELECT COUNT(*) FROM \"mOpenAxisValueRestriction\"");
        Assert.Equal(58, reference);

        var join =
            """
            FROM "mOpenAxisValueRestriction" r
            JOIN "mAxis" a           ON a."AxisID" = r."AxisID"
            JOIN "mTableAxis" ta     ON ta."AxisID" = a."AxisID"
            JOIN "mTable" tab        ON tab."TableID" = ta."TableID"
            JOIN "mTaxonomyTable" tt ON tt."TableID" = tab."TableID"
            JOIN "mTaxonomy" t       ON t."TaxonomyID" = tt."TaxonomyID"
            """;
        var generated = GeneratedFilteredCount("r.\"AxisID\" || '/' || r.\"HierarchyID\"", join, "t.\"TaxonomyCode\"");
        Assert.Equal(58, generated);
    }

    // ------------------------------------------------------------------
    // mTableAxis: CLASS SEQUENCE per table: X < closed Y < closed Z < open Y < open Z, IN ORDER,
    // for all 217 tables. Reproduced by table business key, without comparing any ID.
    // ------------------------------------------------------------------

    private static string ClassOf(string orientation, bool isOpenAxis) => (orientation, isOpenAxis) switch
    {
        ("X", false) => "X",
        ("Y", false) => "Y-closed",
        ("Z", false) => "Z-closed",
        ("Y", true) => "Y-open",
        ("Z", true) => "Z-open",
        _ => throw new InvalidOperationException($"Unexpected combination: {orientation}/{isOpenAxis}"),
    };

    private Dictionary<BusinessKeySupport.TableKey, List<string>> ClassSequenceByTable(
        Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var rows = QueryHelpers.Rows(
            connection,
            """
            SELECT t."TaxonomyCode", tab."TableCode", ta."Order", a."AxisOrientation", a."IsOpenAxis"
            FROM "mTaxonomyTable" tt
            JOIN "mTaxonomy" t     ON t."TaxonomyID" = tt."TaxonomyID"
            JOIN "mTable" tab      ON tab."TableID"  = tt."TableID"
            JOIN "mTableAxis" ta   ON ta."TableID"   = tab."TableID"
            JOIN "mAxis" a         ON a."AxisID"     = ta."AxisID"
            """,
            5);

        var result = new Dictionary<BusinessKeySupport.TableKey, List<string>>();
        foreach (var group in rows
                     .Select(r => (
                         Taxonomy: BusinessKeySupport.NormalizeTaxonomyCode(r[0]!),
                         Table: r[1]!,
                         Order: int.Parse(r[2]!),
                         Class: ClassOf(r[3]!, r[4] == "1")))
                     .Where(r => TargetTaxonomies.Contains(r.Taxonomy))
                     .GroupBy(r => new BusinessKeySupport.TableKey(r.Taxonomy, r.Table)))
        {
            result[group.Key] = group.OrderBy(g => g.Order).Select(g => g.Class).ToList();
        }

        return result;
    }

    [DataFact]
    public void TableAxis_ClassSequence_MatchesReference32_ForAllTables()
    {
        var generated = ClassSequenceByTable(_fixture.GeneratedConnection);
        var reference = ClassSequenceByTable(_fixture.Reference32Connection);

        Assert.Equal(217, reference.Count);

        var failures = new List<string>();
        foreach (var (key, refSequence) in reference)
        {
            if (!generated.TryGetValue(key, out var genSequence))
            {
                failures.Add($"{key.Taxonomy}/{key.TableCode}: table missing from the generated output");
                continue;
            }

            if (!genSequence.SequenceEqual(refSequence))
            {
                failures.Add(
                    $"{key.Taxonomy}/{key.TableCode}: generated=[{string.Join(",", genSequence)}] reference=[{string.Join(",", refSequence)}]");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count}/217 tables with a class sequence different from the 3.2 reference:\n" + string.Join("\n", failures.Take(20)));
    }

    // ------------------------------------------------------------------
    // mAxis.AxisLabel of CLOSED axes: literal from the Access if present, otherwise a constant
    // per orientation. 433/433 in 3.2, with ONE declared legitimate exception:
    // COREP_3.2/C_67.00.a = 'Total currencies'.
    // ------------------------------------------------------------------

    private Dictionary<(BusinessKeySupport.TableKey Table, string Orientation), string?> ClosedAxisLabelsByTable(
        Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var rows = QueryHelpers.Rows(
            connection,
            """
            SELECT t."TaxonomyCode", tab."TableCode", a."AxisOrientation", a."AxisLabel"
            FROM "mTaxonomyTable" tt
            JOIN "mTaxonomy" t     ON t."TaxonomyID" = tt."TaxonomyID"
            JOIN "mTable" tab      ON tab."TableID"  = tt."TableID"
            JOIN "mTableAxis" ta   ON ta."TableID"   = tab."TableID"
            JOIN "mAxis" a         ON a."AxisID"     = ta."AxisID"
            WHERE a."IsOpenAxis" = 0
            """,
            4);

        var result = new Dictionary<(BusinessKeySupport.TableKey, string), string?>();
        foreach (var row in rows)
        {
            var taxonomy = BusinessKeySupport.NormalizeTaxonomyCode(row[0]!);
            if (!TargetTaxonomies.Contains(taxonomy))
            {
                continue;
            }

            var key = (new BusinessKeySupport.TableKey(taxonomy, row[1]!), row[2]!);
            result[key] = row[3];
        }

        return result;
    }

    [DataFact]
    public void AxisLabel_OfClosedAxes_MatchesReference32_WithOneDeclaredException()
    {
        var generated = ClosedAxisLabelsByTable(_fixture.GeneratedConnection);
        var reference = ClosedAxisLabelsByTable(_fixture.Reference32Connection);

        Assert.Equal(433, reference.Count);

        var declaredException = (new BusinessKeySupport.TableKey("corep_3.2", "C_67.00.a"), "Z");

        var failures = new List<string>();
        foreach (var (key, refLabel) in reference)
        {
            if (!generated.TryGetValue(key, out var genLabel))
            {
                failures.Add($"{key.Item1.Taxonomy}/{key.Item1.TableCode}/{key.Item2}: missing from the generated output");
                continue;
            }

            if (!string.Equals(genLabel, refLabel, StringComparison.Ordinal))
            {
                if (key.Equals(declaredException))
                {
                    continue; // The single known legitimate exception.
                }

                failures.Add($"{key.Item1.Taxonomy}/{key.Item1.TableCode}/{key.Item2}: generated='{genLabel}' reference='{refLabel}'");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} closed-axis AxisLabel values do not match (outside the declared exception):\n" + string.Join("\n", failures.Take(20)));
    }

    // ------------------------------------------------------------------
    // mAxisOrdinate.OrdinateLabel of CLOSED axes: whitespace-insensitive (100% with that
    // normalisation; the literal residue is CRLF handling, with no content differences). Matched
    // by OrdinateCode path (OrdinateCode is the most sensitive criterion of the project).
    // ------------------------------------------------------------------

    private static string CollapseWhitespace(string? s) =>
        s is null ? string.Empty : string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [DataFact]
    public void OrdinateLabel_OfClosedAxes_MatchesReference32_WhitespaceInsensitive()
    {
        var normalizedTaxonomies = TargetTaxonomies;
        var tableIdsGenerated = BusinessKeySupport.BuildTableIdsByBusinessKey(_fixture.GeneratedConnection, normalizedTaxonomies);
        var tableIdsReference = BusinessKeySupport.BuildTableIdsByBusinessKey(_fixture.Reference32Connection, normalizedTaxonomies);

        Assert.Equal(217, tableIdsReference.Count);

        var generatedIndex = BusinessKeySupport.BuildClosedOrdinatePathIndex(_fixture.GeneratedConnection, tableIdsGenerated.Values.ToHashSet());
        var referenceIndex = BusinessKeySupport.BuildClosedOrdinatePathIndex(_fixture.Reference32Connection, tableIdsReference.Values.ToHashSet());

        var generatedLabels = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"OrdinateID\", \"OrdinateLabel\" FROM \"mAxisOrdinate\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]);
        var referenceLabels = QueryHelpers.Rows(_fixture.Reference32Connection, "SELECT \"OrdinateID\", \"OrdinateLabel\" FROM \"mAxisOrdinate\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]);

        var compared = 0;
        var failures = new List<string>();

        foreach (var (tableKey, refTableId) in tableIdsReference)
        {
            if (!tableIdsGenerated.TryGetValue(tableKey, out var genTableId))
            {
                continue; // checked elsewhere (table census)
            }

            foreach (var ((tableId, axis, path), refOrdinateId) in referenceIndex)
            {
                if (tableId != refTableId)
                {
                    continue;
                }

                if (!generatedIndex.TryGetValue((genTableId, axis, path), out var genOrdinateId))
                {
                    failures.Add($"{tableKey.Taxonomy}/{tableKey.TableCode}/{axis}/{path}: ordinate missing from the generated output");
                    continue;
                }

                compared++;
                var refLabel = CollapseWhitespace(referenceLabels.GetValueOrDefault(refOrdinateId));
                var genLabel = CollapseWhitespace(generatedLabels.GetValueOrDefault(genOrdinateId));

                if (!string.Equals(refLabel, genLabel, StringComparison.Ordinal))
                {
                    failures.Add($"{tableKey.Taxonomy}/{tableKey.TableCode}/{axis}/{path}: generated='{genLabel}' reference='{refLabel}'");
                }
            }
        }

        Assert.True(compared > 7000, $"Only {compared} ordinates were compared: path matching failed wholesale.");
        Assert.True(failures.Count == 0, $"{failures.Count}/{compared} different OrdinateLabel values (whitespace-insensitive):\n" + string.Join("\n", failures.Take(20)));
    }

    // ------------------------------------------------------------------
    // mOpenAxisValueRestriction: HierarchyID (normalising case and the _REL_n suffix),
    // HierarchyStartingMemberID (by MemberCode) and IsStartingMemberIncluded: 58/58, literal
    // against the primary reference.
    // ------------------------------------------------------------------

    private static string NormalizeHierarchyCode(string code)
    {
        var upper = code.Trim().ToUpperInvariant();
        var relIndex = upper.IndexOf("_REL_", StringComparison.Ordinal);
        return relIndex >= 0 ? upper[..relIndex] : upper;
    }

    private sealed record RestrictionRow(string Taxonomy, string TableCode, string Axis, string HierarchyCode, string? StartMemberCode, bool? StartIncluded);

    private List<RestrictionRow> RestrictionRows(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var rows = QueryHelpers.Rows(
            connection,
            """
            SELECT t."TaxonomyCode", tab."TableCode", a."AxisOrientation",
                   h."HierarchyCode", mem."MemberCode", r."IsStartingMemberIncluded",
                   dim."DimensionXBRLCode"
            FROM "mOpenAxisValueRestriction" r
            JOIN "mAxis" a          ON a."AxisID" = r."AxisID"
            JOIN "mTableAxis" ta    ON ta."AxisID" = a."AxisID"
            JOIN "mTable" tab       ON tab."TableID" = ta."TableID"
            JOIN "mTaxonomyTable" tt ON tt."TableID" = tab."TableID"
            JOIN "mTaxonomy" t      ON t."TaxonomyID" = tt."TaxonomyID"
            JOIN "mHierarchy" h     ON h."HierarchyID" = r."HierarchyID"
            LEFT JOIN "mMember" mem ON mem."MemberID" = r."HierarchyStartingMemberID"
            JOIN "mAxisOrdinate" o  ON o."AxisID" = a."AxisID"
            JOIN "mOrdinateCategorisation" oc ON oc."OrdinateID" = o."OrdinateID" AND oc."MemberID" = 9999
            JOIN "mDimension" dim   ON dim."DimensionID" = oc."DimensionID"
            """,
            7);

        // Disambiguate multiple open axes of the same orientation (e.g. two open Z axes in the
        // same table) by the XBRL CODE of the open dimension they represent (the MemberID=9999
        // categorisation of the single ordinate of the axis). AxisLabel and OrdinateCode are NOT
        // used: the former differs ON PURPOSE between 3.2 and the generated output for key Y axes
        // and for some Z axes; the latter is NULL on the 47 open Z axes of 3.2. The dimension, in
        // contrast, is the same business entity in both databases.
        return rows
            .Select(r => (
                Taxonomy: BusinessKeySupport.NormalizeTaxonomyCode(r[0]!),
                Table: r[1]!,
                Axis: r[2]! + "/" + r[6],
                Hierarchy: r[3]!,
                Member: r[4],
                Included: r[5] is null ? (bool?)null : r[5] == "1"))
            .Where(r => TargetTaxonomies.Contains(r.Taxonomy))
            .Select(r => new RestrictionRow(r.Taxonomy, r.Table, r.Axis, r.Hierarchy, r.Member, r.Included))
            .ToList();
    }

    [DataFact]
    public void OpenAxisValueRestriction_ContentMatchesReference32_58Rows()
    {
        var generated = RestrictionRows(_fixture.GeneratedConnection)
            .ToDictionary(r => (r.Taxonomy, r.TableCode, r.Axis));
        var reference = RestrictionRows(_fixture.Reference32Connection)
            .ToDictionary(r => (r.Taxonomy, r.TableCode, r.Axis));

        Assert.Equal(58, reference.Count);

        var hierarchyFailures = new List<string>();
        var memberFailures = new List<string>();
        var includedFailures = new List<string>();
        var missing = new List<string>();

        foreach (var (key, refRow) in reference)
        {
            if (!generated.TryGetValue(key, out var genRow))
            {
                missing.Add($"{key.Taxonomy}/{key.TableCode}/{key.Axis}");
                continue;
            }

            if (!string.Equals(NormalizeHierarchyCode(genRow.HierarchyCode), NormalizeHierarchyCode(refRow.HierarchyCode), StringComparison.Ordinal))
            {
                hierarchyFailures.Add($"{key.Taxonomy}/{key.TableCode}/{key.Axis}: generated='{genRow.HierarchyCode}' reference='{refRow.HierarchyCode}'");
            }

            if (!string.Equals(genRow.StartMemberCode, refRow.StartMemberCode, StringComparison.Ordinal))
            {
                memberFailures.Add($"{key.Taxonomy}/{key.TableCode}/{key.Axis}: generated='{genRow.StartMemberCode}' reference='{refRow.StartMemberCode}'");
            }

            if (genRow.StartIncluded != refRow.StartIncluded)
            {
                includedFailures.Add($"{key.Taxonomy}/{key.TableCode}/{key.Axis}: generated={genRow.StartIncluded} reference={refRow.StartIncluded}");
            }
        }

        Assert.True(missing.Count == 0, $"{missing.Count}/58 restrictions missing from the generated output:\n" + string.Join("\n", missing.Take(20)));
        Assert.True(hierarchyFailures.Count == 0, $"{hierarchyFailures.Count}/58 different HierarchyID values (case and REL normalised):\n" + string.Join("\n", hierarchyFailures.Take(20)));
        Assert.True(memberFailures.Count == 0, $"{memberFailures.Count}/58 different HierarchyStartingMemberID values (by MemberCode):\n" + string.Join("\n", memberFailures.Take(20)));
        Assert.True(includedFailures.Count == 0, $"{includedFailures.Count}/58 different IsStartingMemberIncluded values:\n" + string.Join("\n", includedFailures.Take(20)));
    }
}
