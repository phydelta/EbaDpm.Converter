namespace EbaDpm.Converter.Distribution.Tests.Dpm2;

/// <summary>
/// Synthetic coverage of the table-level metric (<c>TableVersion.PropertyID</c>) in the DPM 2.0 axis
/// and cell loader, from <c>docs/mapping-dpm2.md</c> ("mOrdinateCategorisation" step 3 and
/// "Signatures"): when no non-key header contributes a metric, <c>MET(&lt;metric&gt;)</c> is added to
/// the ordinates of the FIRST closed axis in X, Y, Z order; a table-level metric that cannot be
/// resolved is counted as an unresolved metric pair and reported as a structural anomaly, nothing
/// invented; a metric declared by a non-key header wins and the table-level one is ignored.
/// Expected values are derived from that documentation, never from running the code.
/// </summary>
public sealed class TableLevelMetricSyntheticTests
{
    [Fact]
    public void TableMetric_IsAddedToTheOrdinatesOfTheFirstClosedAxis_AndReachesEveryCell()
    {
        var w = new AxisCellWorld();
        w.TablePropertyId = w.AddMetric("qCFB");
        var x1 = w.AddHeader("X", "0010");
        var x2 = w.AddHeader("X", "0020");
        var y1 = w.AddHeader("Y", "0100");
        var y2 = w.AddHeader("Y", "0200");
        foreach (var x in new[] { x1, x2 })
        {
            foreach (var y in new[] { y1, y2 })
            {
                w.AddCell(x, y);
            }
        }

        using var outcome = w.Run();

        // First closed axis in X, Y, Z order is X: its ordinates carry the metric, the rows do not.
        Assert.Equal(["MET=qCFB"], outcome.Categorisation("0010"));
        Assert.Equal(["MET=qCFB"], outcome.Categorisation("0020"));
        Assert.Empty(outcome.Categorisation("0100"));
        Assert.Empty(outcome.Categorisation("0200"));

        var cells = outcome.Cells();
        Assert.Equal(4, cells.Count);
        Assert.All(cells, c =>
        {
            Assert.Equal(0, c.IsShaded);
            Assert.Equal("MET(eba_qCFB)", c.Dps);
            Assert.Equal("MET(eba_qCFB)", c.DatapointSignature);
        });
        Assert.Equal(0, outcome.Result.UnresolvedMetricPairs);
    }

    [Fact]
    public void TableMetric_WithOnlyAYClosedAxis_GoesToTheYOrdinates()
    {
        var w = new AxisCellWorld();
        w.TablePropertyId = w.AddMetric("qCFB");
        var y1 = w.AddHeader("Y", "0100");
        w.AddCell(column: null, row: y1);

        using var outcome = w.Run();

        Assert.Equal(["MET=qCFB"], outcome.Categorisation("0100"));
        Assert.Equal("MET(eba_qCFB)", Assert.Single(outcome.Cells()).Dps);
    }

    [Fact]
    public void TableMetric_WithXAndZ_GoesToTheXOrdinatesNotTheZOnes()
    {
        var w = new AxisCellWorld();
        w.TablePropertyId = w.AddMetric("qCFB");
        var x1 = w.AddHeader("X", "0010");
        var z1 = w.AddHeader("Z", "0001");
        w.AddCell(x1, sheet: z1);

        using var outcome = w.Run();

        Assert.Equal(["MET=qCFB"], outcome.Categorisation("0010"));
        Assert.Empty(outcome.Categorisation("0001"));
    }

    /// <summary>Positive control for the test above: with a non-key header metric the table-level metric is ignored.</summary>
    [Fact]
    public void TableMetric_IsIgnored_WhenANonKeyHeaderDeclaresAMetric()
    {
        var w = new AxisCellWorld();
        var headerMetric = w.AddMetric("met1");
        w.TablePropertyId = w.AddMetric("qCFB");
        var column = w.AddHeader("X", "0010", propertyId: headerMetric);
        w.AddCell(column);

        using var outcome = w.Run();

        Assert.Equal(["MET=met1"], outcome.Categorisation("0010"));
        var cell = Assert.Single(outcome.Cells());
        Assert.Equal("MET(eba_met1)", cell.Dps);
        Assert.DoesNotContain("qCFB", cell.Dps!, StringComparison.Ordinal);
        Assert.Equal(0, outcome.Count("SELECT COUNT(1) FROM \"mOrdinateCategorisation\" oc JOIN \"mMember\" m ON m.\"MemberID\" = oc.\"MemberID\" WHERE m.\"MemberCode\" = 'qCFB'"));
        Assert.Equal(0, outcome.Result.UnresolvedMetricPairs);
    }

    /// <summary>Same world as above without the header metric: the table-level metric is added (the control for "ignored").</summary>
    [Fact]
    public void TableMetric_SameWorldWithoutTheHeaderMetric_IsAdded()
    {
        var w = new AxisCellWorld();
        w.AddMetric("met1");
        w.TablePropertyId = w.AddMetric("qCFB");
        var column = w.AddHeader("X", "0010");
        w.AddCell(column);

        using var outcome = w.Run();

        Assert.Equal("MET(eba_qCFB)", Assert.Single(outcome.Cells()).Dps);
    }

    [Fact]
    public void TableMetric_Unresolvable_IsCountedAndReported_AndNothingIsInvented()
    {
        var w = new AxisCellWorld();
        w.TablePropertyId = w.AddMetric("qCFB", withDestinationMember: false);
        var x1 = w.AddHeader("X", "0010");
        var y1 = w.AddHeader("Y", "0100");
        w.AddCell(x1, y1);

        using var outcome = w.Run();

        Assert.Equal(1, outcome.Result.UnresolvedMetricPairs);
        var anomaly = Assert.Single(outcome.Result.StructuralAnomalies, a => a.Contains("table-level metric", StringComparison.Ordinal));
        Assert.Contains("TableVID=1", anomaly, StringComparison.Ordinal);
        Assert.Contains("T_01", anomaly, StringComparison.Ordinal);
        Assert.Contains("qCFB", anomaly, StringComparison.Ordinal);

        Assert.Empty(outcome.Categorisation("0010"));
        Assert.Empty(outcome.Categorisation("0100"));
        var cell = Assert.Single(outcome.Cells());
        Assert.DoesNotContain("MET(", cell.Dps ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("MET(", cell.DatapointSignature ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void NoTableMetric_ChangesNothing_AndReportsNoAnomaly()
    {
        var w = new AxisCellWorld();
        var metric = w.AddMetric("met1");
        var column = w.AddHeader("X", "0010", propertyId: metric);
        w.AddCell(column);

        using var outcome = w.Run();

        Assert.Equal("MET(eba_met1)", Assert.Single(outcome.Cells()).Dps);
        Assert.Equal(0, outcome.Result.UnresolvedMetricPairs);
        Assert.DoesNotContain(outcome.Result.StructuralAnomalies, a => a.Contains("table-level metric", StringComparison.Ordinal));
    }
}
