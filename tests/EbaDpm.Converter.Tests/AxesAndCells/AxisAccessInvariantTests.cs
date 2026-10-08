using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.AxesAndCells;

/// <summary>
/// Invariants checked against the SOURCE Access database, not against any SQLite reference: the
/// per-table census of axes and ordinates, and three "free" invariants that hold on the whole
/// Access database and on every reference, reproduced here without comparing any ID.
/// </summary>
[Collection("AxesAndCells")]
[Trait("Tier", "RealData")]
public sealed class AxisAccessInvariantTests
{
    private readonly AxisAndCellFixture _fixture;

    public AxisAccessInvariantTests(AxisAndCellFixture fixture)
    {
        _fixture = fixture;
    }

    // ------------------------------------------------------------------
    // "Typed domain <=> no default member", in both directions, over the WHOLE Access database
    // (not only the selected universe: it is a property of the dictionary, not of the taxonomy
    // selection).
    // ------------------------------------------------------------------

    [DataFact]
    public void TypedDomains_NeverHaveADefaultMember_AndViceVersa()
    {
        var domains = _fixture.AccessReader.ReadDomains().ToList();
        var domainIdsWithDefaultMember = _fixture.AccessReader.ReadMembers()
            .Where(m => m.IsDefaultMember && m.DomainId is not null)
            .Select(m => m.DomainId!.Value)
            .ToHashSet();

        Assert.True(domains.Count > 0);

        var typedWithDefault = domains.Where(d => d.IsTypedDomain && domainIdsWithDefaultMember.Contains(d.DomainId)).ToList();
        var untypedWithoutDefault = domains.Where(d => !d.IsTypedDomain && !domainIdsWithDefaultMember.Contains(d.DomainId)).ToList();

        Assert.True(
            typedWithDefault.Count == 0,
            $"{typedWithDefault.Count} TYPED domains have a default member (expected 0): " +
            string.Join(",", typedWithDefault.Take(10).Select(d => d.DomainId)));

        // An exact count is not hard-coded (the Access universe may grow between releases), but
        // 0 would be a serious dictionary regression: it would mean that NO untyped domain
        // lacks a default member.
        Assert.True(
            untypedWithoutDefault.Count > 0,
            "0 untyped domains without a default member: contradicts the measured behaviour of the Access v4.1 (12 such domains). Has the source changed?");
    }

    // ------------------------------------------------------------------
    // "Exactly 1 of the Access dimensions has a NULL DimensionXbrlCode, and it is the metric
    // dimension": independent corroboration that the Access models the metric as a real
    // Dimension. Checked in two halves: the Access (the NULL dimension is unique) and the
    // generated output (there is exactly one mDimension.DimensionXBRLCode = 'MET').
    // ------------------------------------------------------------------

    [DataFact]
    public void ExactlyOneAccessDimension_HasNullXbrlCode()
    {
        var dimensions = _fixture.AccessReader.ReadDimensions().ToList();
        Assert.True(dimensions.Count > 0);

        var withNullXbrlCode = dimensions.Where(d => d.DimensionXbrlCode is null).ToList();
        Assert.True(
            withNullXbrlCode.Count == 1,
            $"{withNullXbrlCode.Count} dimensions with NULL DimensionXbrlCode (exactly 1 expected): " +
            string.Join(",", withNullXbrlCode.Select(d => d.DimensionId)));
    }

    [DataFact]
    public void GeneratedDictionary_HasExactlyOneMetricDimension_WithXbrlCode_MET()
    {
        var count = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mDimension\" WHERE \"DimensionXBRLCode\" = 'MET'");
        Assert.Equal(1, count);
    }

    // ------------------------------------------------------------------
    // Per-table census: the number of axes and ordinates PREDICTED from the Access database by
    // applying the transformation rules literally must match EXACTLY table by table, not only
    // in aggregate (a global difference of 0 with per-table differences would be a masked
    // failure).
    // ------------------------------------------------------------------

    [DataFact]
    public void OrdinateAndAxisCensus_MatchesThePredictionFromAccess_TableByTable()
    {
        var tableIdByTableVId = _fixture.TemplateOrTableResult.TableIdByTableVId;
        var tableVIds = tableIdByTableVId.Keys.ToList();
        Assert.True(tableVIds.Count > 0);

        var axesByTableVId = _fixture.AccessReader.ReadAxesByTableVIds(tableVIds)
            .Where(a => a.AxisOrientation != "O")
            .GroupBy(a => a.TableVId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var ordinatesByAxisId = _fixture.AccessReader.ReadAxisOrdinatesByTableVIds(tableVIds)
            .GroupBy(o => o.AxisId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var categorisationsByOrdinateId = _fixture.AccessReader.ReadOrdinateCategorisationsByTableVIds(tableVIds)
            .GroupBy(c => c.OrdinateId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var actualCounts = QueryHelpers.Rows(
                _fixture.GeneratedConnection,
                """
                SELECT ta."TableID", COUNT(DISTINCT ta."AxisID"), COUNT(o."OrdinateID")
                FROM "mTableAxis" ta
                LEFT JOIN "mAxisOrdinate" o ON o."AxisID" = ta."AxisID"
                GROUP BY ta."TableID"
                """,
                3)
            .ToDictionary(r => int.Parse(r[0]!), r => (Axes: int.Parse(r[1]!), Ordinates: int.Parse(r[2]!)));

        var failures = new List<string>();

        foreach (var tableVId in tableVIds)
        {
            var tableId = tableIdByTableVId[tableVId];
            var axes = axesByTableVId.GetValueOrDefault(tableVId, []);

            var predictedAxes = 0;
            var predictedOrdinates = 0;

            foreach (var axis in axes)
            {
                var ordinates = ordinatesByAxisId.GetValueOrDefault(axis.AxisId, []);

                if (!axis.IsOpenAxis)
                {
                    predictedAxes += 1;

                    // X columns with IsRowKey=1 are EXCLUDED from the target X axis (they are
                    // counted separately, below, as the open Y axes they generate). Without this
                    // deduction they would be counted twice.
                    var survivorCount = axis.AxisOrientation == "X"
                        ? ordinates.Count(o => !o.IsRowKey)
                        : ordinates.Count;
                    predictedOrdinates += survivorCount;
                    continue;
                }

                if (axis.AxisOrientation == "Y")
                {
                    // Each X column with IsRowKey=1 generates its own open Y axis; the Access
                    // open Y axis itself (its sentinel) disappears.
                    continue;
                }

                if (axis.AxisOrientation == "Z")
                {
                    var sentinel = ordinates.SingleOrDefault();
                    if (sentinel is null)
                    {
                        continue;
                    }

                    var dimensionCount = categorisationsByOrdinateId.GetValueOrDefault(sentinel.OrdinateId, [])
                        .Where(c => c.MemberId == 999)
                        .Select(c => c.DimensionId)
                        .Distinct()
                        .Count();

                    predictedAxes += dimensionCount;
                    predictedOrdinates += dimensionCount;
                }
            }

            // X columns with IsRowKey=1: each one generates an open Y axis with 1 ordinate.
            var xAxis = axes.SingleOrDefault(a => a.AxisOrientation == "X");
            if (xAxis is not null)
            {
                var keyColumns = ordinatesByAxisId.GetValueOrDefault(xAxis.AxisId, []).Count(o => o.IsRowKey);
                predictedAxes += keyColumns;
                predictedOrdinates += keyColumns;
            }

            var actual = actualCounts.GetValueOrDefault(tableId, (Axes: 0, Ordinates: 0));
            if (actual.Axes != predictedAxes || actual.Ordinates != predictedOrdinates)
            {
                failures.Add($"TableVID={tableVId} TableID={tableId}: predicted axes={predictedAxes} actual={actual.Axes}; predicted ordinates={predictedOrdinates} actual={actual.Ordinates}");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} of {tableVIds.Count} tables with a mismatched axis/ordinate census:\n" + string.Join("\n", failures.Take(20)));
    }

    // ------------------------------------------------------------------
    // Canary: Access.Axis.AxisOrder must remain NULL across the converted universe. If that
    // stops being true, AxisAndCellLoader would already have thrown while building the fixture;
    // this test just makes it visible and explicit in the report.
    // ------------------------------------------------------------------

    [DataFact]
    public void AccessAxisOrder_IsAlwaysNull_OnTheConvertedUniverse()
    {
        var tableVIds = _fixture.TemplateOrTableResult.TableIdByTableVId.Keys.ToList();
        var populated = _fixture.AccessReader.ReadAxesByTableVIds(tableVIds).Count(a => a.AxisOrder is not null);
        Assert.Equal(0, populated);
    }
}
