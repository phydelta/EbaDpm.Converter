using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using EbaDpm.Converter.Tests.Dpm2;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>One cell of a layout grid: where it is, and whether the workbook shades it.</summary>
internal sealed record LayoutGridCell(
    string File, string Sheet, string TableCode, string? ZCode, string RowCode, string ColumnCode, string CellRef, bool IsShaded);

/// <summary>
/// The shading decision of the layout extractor against the Access model (#67). A layout cell is
/// DISABLED when the workbook shades it; an Access cell is disabled when it does not exist at that
/// (table, row, column, sheet) coordinate or <c>TableVersionCell.IsExcluded</c> is set
/// (<c>docs/mapping-dpm2.md</c>, <c>IsShaded</c>). The comparison is by business key (table code, row
/// ordinate code, column ordinate code, Z code), never by ID, in BOTH directions.
/// </summary>
internal static class ShadingAgreement
{
    public sealed record Key(string TableCode, string? ZCode, string RowCode, string ColumnCode);

    public sealed record Result(
        long BothEnabled,
        long BothDisabled,
        IReadOnlyList<LayoutGridCell> LayoutShadedAccessEnabled,
        IReadOnlyList<LayoutGridCell> LayoutEnabledAccessDisabledOrAbsent,
        IReadOnlyList<Key> AccessEnabledNotInLayoutGrid,
        IReadOnlyList<string> LayoutTablesWithoutAccessTable,
        long CellsOfTablesWithoutAccessTable)
    {
        public long Compared => BothEnabled + BothDisabled + LayoutShadedAccessEnabled.Count + LayoutEnabledAccessDisabledOrAbsent.Count;

        public long Disagreements =>
            LayoutShadedAccessEnabled.Count + LayoutEnabledAccessDisabledOrAbsent.Count + AccessEnabledNotInLayoutGrid.Count + LayoutTablesWithoutAccessTable.Count;
    }

    /// <summary>
    /// Compares layout shading with Access enabled/disabled status by business key and returns agreement/disagreement aggregates.
    /// </summary>
    /// <param name="accessEnabled">For every Access cell: whether it is enabled (exists and is not excluded). An absent key is a cell that does not exist.</param>
    public static Result Compare(IEnumerable<LayoutGridCell> layout, IReadOnlyDictionary<Key, bool> accessEnabled)
    {
        var accessTables = accessEnabled.Keys.Select(k => k.TableCode).ToHashSet(StringComparer.Ordinal);
        var grid = new HashSet<Key>();
        long bothEnabled = 0, bothDisabled = 0, withoutTable = 0;
        var shadedButEnabled = new List<LayoutGridCell>();
        var enabledButDisabled = new List<LayoutGridCell>();
        var tablesWithout = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var cell in layout)
        {
            if (!accessTables.Contains(cell.TableCode))
            {
                tablesWithout.Add(cell.TableCode);
                withoutTable++;
                continue;
            }

            var key = new Key(cell.TableCode, cell.ZCode, cell.RowCode, cell.ColumnCode);
            grid.Add(key);
            var enabledInAccess = accessEnabled.TryGetValue(key, out var e) && e;
            switch (cell.IsShaded, enabledInAccess)
            {
                case (false, true): bothEnabled++; break;
                case (true, false): bothDisabled++; break;
                case (true, true): shadedButEnabled.Add(cell); break;
                default: enabledButDisabled.Add(cell); break;
            }
        }

        var layoutTables = layout.Select(c => c.TableCode).ToHashSet(StringComparer.Ordinal);
        var notInGrid = accessEnabled
            .Where(kv => kv.Value && layoutTables.Contains(kv.Key.TableCode) && !grid.Contains(kv.Key))
            .Select(kv => kv.Key)
            .ToList();
        return new Result(bothEnabled, bothDisabled, shadedButEnabled, enabledButDisabled, notInGrid, [.. tablesWithout], withoutTable);
    }

    /// <summary>
    /// ONE named known exception, by business key (decided 2026-11-20): table <c>K_14.00</c>, X header code
    /// <c>10</c>. The Access database declares the column header code <c>10</c> where the layout draws
    /// <c>0010</c> (13 cells); this is a quirk of the source, and the converter emits what Access declares.
    /// The exception removes exactly the pairs (layout cell with column <c>0010</c>, Access cell with column
    /// <c>10</c>, same table, Z code and row code). It never matches by pattern, and it is STALE (reported in
    /// <see cref="ExceptionOutcome.StaleReasons"/>) unless every one of the expected pairs occurs exactly once
    /// and nothing else of K_14.00 is involved.
    /// </summary>
    public static class K1400HeaderCode10
    {
        public const string Table = "K_14.00";
        public const string AccessColumnCode = "10";
        public const string LayoutColumnCode = "0010";
        public const int ExpectedPairs = 13;
        public const string Reason = "Access declares the X header code '10' where the layout draws '0010' (13 cells); source quirk, the output emits what Access declares.";
        public const string Date = "2026-11-20";
    }

    public sealed record ExceptionOutcome(Result Remaining, int PairsExcused, IReadOnlyList<string> StaleReasons);

    /// <summary>Applies <see cref="K1400HeaderCode10"/>, pair by pair, and reports why it is stale when it does not match as declared.</summary>
    public static ExceptionOutcome ApplyK1400HeaderCode10(Result r)
    {
        var layoutSide = r.LayoutEnabledAccessDisabledOrAbsent
            .Where(c => c.TableCode == K1400HeaderCode10.Table && c.ColumnCode == K1400HeaderCode10.LayoutColumnCode)
            .ToList();
        var accessSide = r.AccessEnabledNotInLayoutGrid
            .Where(k => k.TableCode == K1400HeaderCode10.Table && k.ColumnCode == K1400HeaderCode10.AccessColumnCode)
            .ToList();

        var accessByCoordinate = accessSide.GroupBy(k => (k.ZCode, k.RowCode)).ToDictionary(g => g.Key, g => g.ToList());
        var pairedLayout = new List<LayoutGridCell>();
        var pairedAccess = new List<Key>();
        foreach (var cell in layoutSide)
        {
            if (accessByCoordinate.TryGetValue((cell.ZCode, cell.RowCode), out var keys) && keys.Count == 1 && !pairedAccess.Contains(keys[0]))
            {
                pairedLayout.Add(cell);
                pairedAccess.Add(keys[0]);
            }
        }

        var stale = new List<string>();
        if (pairedLayout.Count != K1400HeaderCode10.ExpectedPairs)
        {
            stale.Add($"known exception K_14.00 header code '10' vs '0010' is stale: {pairedLayout.Count} matching pair(s), {K1400HeaderCode10.ExpectedPairs} declared");
        }

        if (layoutSide.Count != pairedLayout.Count || accessSide.Count != pairedAccess.Count)
        {
            stale.Add($"known exception K_14.00: {layoutSide.Count - pairedLayout.Count} layout cell(s) and {accessSide.Count - pairedAccess.Count} Access cell(s) of the named column are not part of a declared pair");
        }

        var remaining = r with
        {
            LayoutEnabledAccessDisabledOrAbsent = [.. r.LayoutEnabledAccessDisabledOrAbsent.Where(c => !pairedLayout.Contains(c))],
            AccessEnabledNotInLayoutGrid = [.. r.AccessEnabledNotInLayoutGrid.Where(k => !pairedAccess.Contains(k))],
        };
        return new ExceptionOutcome(remaining, pairedLayout.Count, stale);
    }

    public static string Describe(Result r) =>
        $"compared {r.Compared} cells: both enabled {r.BothEnabled}, both disabled {r.BothDisabled}, " +
        $"layout shaded but Access enabled {r.LayoutShadedAccessEnabled.Count}, " +
        $"layout enabled but Access excluded/absent {r.LayoutEnabledAccessDisabledOrAbsent.Count}, " +
        $"Access enabled but not in the layout grid {r.AccessEnabledNotInLayoutGrid.Count}; " +
        $"{r.LayoutTablesWithoutAccessTable.Count} layout table code(s) with no Access table version ({r.CellsOfTablesWithoutAccessTable} cells not compared: " +
        $"{string.Join(", ", r.LayoutTablesWithoutAccessTable.Select(t => $"'{t}'"))})";
}

/// <summary>Positive control of <see cref="ShadingAgreement"/>: it must be able to find a disagreement in each direction.</summary>
[Trait("Tier", "Synthetic")]
public sealed class ShadingAgreementSyntheticTests
{
    private static LayoutGridCell Cell(string table, string row, string col, bool shaded, string? z = null) =>
        new("f.xlsx", table, table, z, row, col, "A1", shaded);

    private static Dictionary<ShadingAgreement.Key, bool> Access(params (string Table, string Row, string Col, bool Enabled)[] cells) =>
        cells.ToDictionary(c => new ShadingAgreement.Key(c.Table, null, c.Row, c.Col), c => c.Enabled);

    private static readonly LayoutGridCell[] Agreeing =
    [
        Cell("T_01", "0010", "0010", shaded: false),
        Cell("T_01", "0010", "0020", shaded: true),
        Cell("T_01", "0020", "0010", shaded: true),
    ];

    [Fact]
    public void Agreeing_Grid_HasNoDisagreement_AndCountsBothKinds()
    {
        var access = Access(("T_01", "0010", "0010", true), ("T_01", "0010", "0020", false)); // (0020,0010) does not exist: disabled too

        var r = ShadingAgreement.Compare(Agreeing, access);

        Assert.Equal((1L, 2L), (r.BothEnabled, r.BothDisabled));
        Assert.Equal(0, r.Disagreements);
    }

    [Fact]
    public void ALayoutShadedCellThatAccessEnables_IsFound()
    {
        var access = Access(("T_01", "0010", "0010", true), ("T_01", "0010", "0020", true), ("T_01", "0020", "0010", false));

        var r = ShadingAgreement.Compare(Agreeing, access);

        Assert.Equal(("0010", "0020"), (Assert.Single(r.LayoutShadedAccessEnabled).RowCode, r.LayoutShadedAccessEnabled[0].ColumnCode));
        Assert.Equal(1, r.Disagreements);
    }

    [Fact]
    public void ALayoutEnabledCellThatAccessExcludesOrLacks_IsFound()
    {
        var excluded = Access(("T_01", "0010", "0010", false), ("T_01", "0010", "0020", false));
        var lacking = Access(("T_01", "0010", "0020", false)); // no cell (0010,0010) at all: but T_01 exists

        Assert.Single(ShadingAgreement.Compare(Agreeing, excluded).LayoutEnabledAccessDisabledOrAbsent);
        Assert.Single(ShadingAgreement.Compare(Agreeing, lacking).LayoutEnabledAccessDisabledOrAbsent);
    }

    [Fact]
    public void AnEnabledAccessCellMissingFromTheLayoutGrid_IsFound_InTheOtherDirection()
    {
        var access = Access(("T_01", "0010", "0010", true), ("T_01", "0010", "0020", false), ("T_01", "0030", "0010", true));

        var r = ShadingAgreement.Compare(Agreeing, access);

        Assert.Equal("0030", Assert.Single(r.AccessEnabledNotInLayoutGrid).RowCode);
    }

    [Fact]
    public void ALayoutTableWithNoAccessTable_IsReported_NotSilentlySkipped()
    {
        var access = Access(("OTHER", "0010", "0010", true));

        var r = ShadingAgreement.Compare(Agreeing, access);

        Assert.Equal(["T_01"], r.LayoutTablesWithoutAccessTable);
        Assert.Equal(3, r.CellsOfTablesWithoutAccessTable);
        Assert.Equal(0, r.Compared);
        Assert.True(r.Disagreements > 0);
    }

    [Fact]
    public void TheZCode_IsPartOfTheKey()
    {
        var layout = new[] { Cell("T_01", "0010", "0010", shaded: false, z: "0001"), Cell("T_01", "0010", "0010", shaded: true, z: "0002") };
        var access = new Dictionary<ShadingAgreement.Key, bool>
        {
            [new("T_01", "0001", "0010", "0010")] = true,
            [new("T_01", "0002", "0010", "0010")] = false,
        };

        Assert.Equal(0, ShadingAgreement.Compare(layout, access).Disagreements);

        // Swapping the Z codes of Access turns both cells into disagreements.
        var swapped = new Dictionary<ShadingAgreement.Key, bool>
        {
            [new("T_01", "0001", "0010", "0010")] = false,
            [new("T_01", "0002", "0010", "0010")] = true,
        };
        Assert.Equal(2, ShadingAgreement.Compare(layout, swapped).Disagreements);
    }

    private static LayoutGridCell K14(string row, string col) => new("f.xlsx", "K_14.00", "K_14.00", null, row, col, "A1", false);

    private static ShadingAgreement.Result K14Result(IEnumerable<LayoutGridCell> layoutOnly, IEnumerable<ShadingAgreement.Key> accessOnly) =>
        new(0, 0, [], [.. layoutOnly], [.. accessOnly], [], 0);

    private static IEnumerable<string> K14Rows(int n) => Enumerable.Range(1, n).Select(i => (i * 10).ToString("0000", CultureInfo.InvariantCulture));

    [Fact]
    public void TheK1400Exception_ExcusesExactlyTheThirteenDeclaredPairs()
    {
        var rows = K14Rows(13).ToList();
        var r = K14Result(rows.Select(x => K14(x, "0010")), rows.Select(x => new ShadingAgreement.Key("K_14.00", null, x, "10")));

        var outcome = ShadingAgreement.ApplyK1400HeaderCode10(r);

        Assert.Equal(13, outcome.PairsExcused);
        Assert.Empty(outcome.StaleReasons);
        Assert.Equal(0, outcome.Remaining.Disagreements);
    }

    [Fact]
    public void TheK1400Exception_IsStale_WhenItNoLongerOccurs_PositiveControlOfTheStaleCheck()
    {
        var outcome = ShadingAgreement.ApplyK1400HeaderCode10(K14Result([], []));

        Assert.Equal(0, outcome.PairsExcused);
        Assert.NotEmpty(outcome.StaleReasons);
    }

    [Fact]
    public void TheK1400Exception_IsStale_WhenOnlyPartOfItOccurs()
    {
        var rows = K14Rows(12).ToList();
        var r = K14Result(rows.Select(x => K14(x, "0010")), rows.Select(x => new ShadingAgreement.Key("K_14.00", null, x, "10")));

        Assert.NotEmpty(ShadingAgreement.ApplyK1400HeaderCode10(r).StaleReasons);
    }

    [Fact]
    public void TheK1400Exception_DoesNotHideAnotherCellOfTheSameTable_OrAnotherTable()
    {
        var rows = K14Rows(13).ToList();
        var layoutOnly = rows.Select(x => K14(x, "0010")).Append(K14("0990", "0010")).Append(Cell("K_15.00", "0010", "0010", shaded: false)).ToList();
        var accessOnly = rows.Select(x => new ShadingAgreement.Key("K_14.00", null, x, "10")).Append(new ShadingAgreement.Key("K_14.00", null, "0010", "0020")).ToList();

        var outcome = ShadingAgreement.ApplyK1400HeaderCode10(K14Result(layoutOnly, accessOnly));

        Assert.Equal(13, outcome.PairsExcused);
        Assert.NotEmpty(outcome.StaleReasons); // the unpaired K_14.00 cells of the named column are not excused
        Assert.Equal(2, outcome.Remaining.LayoutEnabledAccessDisabledOrAbsent.Count); // K_14.00 row 0990 and K_15.00
        Assert.Single(outcome.Remaining.AccessEnabledNotInLayoutGrid);
    }
}

/// <summary>
/// The shading of the 4.4 layouts against the DPM 2.0 Access database, in both directions, and the
/// per-fill-colour census that backs the decision of which colours mean "disabled" (#67). Uses the
/// extracted 4.4 repository (<see cref="Dpm2044ValidateFixture.Layouts44DatabasePath"/>); the colour of each
/// cell is read from the workbook by <see cref="FillReader"/>, independently of the production reader.
/// </summary>
[Collection("Dpm2044ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class Layouts44ShadingAgreementRealDataTests(Dpm2044ValidateFixture fixture, ITestOutputHelper output)
{
    private static string Accdb => RepoPaths.AccessDpm20Release44DatabasePath;

    private static string Text(object o) => Convert.ToString(o, CultureInfo.InvariantCulture) ?? string.Empty;

    private List<LayoutGridCell> LayoutGrid()
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Layouts44DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            SELECT f.Path, s.SheetName, s.TableCode, s.ZSuffix, ro.OrdinateCode, co.OrdinateCode, c.CellRef, c.IsShaded
            FROM LayoutCell c
            JOIN LayoutSheet s ON s.SheetId = c.SheetId
            JOIN LayoutFile f ON f.FileId = s.FileId
            JOIN LayoutOrdinate ro ON ro.OrdinateId = c.RowOrdinateId
            JOIN LayoutOrdinate co ON co.OrdinateId = c.ColumnOrdinateId
            """;
        using var r = cmd.ExecuteReader();
        var cells = new List<LayoutGridCell>();
        while (r.Read())
        {
            cells.Add(new LayoutGridCell(
                r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt64(7) == 1));
        }

        return cells;
    }

    /// <summary>
    /// Access cells of the table versions in force at 4.4: (table code, Z code, row code, column code) -> enabled.
    /// Three OLEDB queries joined in memory; the header codes are those of the table version.
    /// </summary>
    private static Dictionary<ShadingAgreement.Key, bool> AccessCells()
    {
        var releaseId = AccessCensus.ReleaseId(Accdb, RepoPaths.Release44Code);
        var inForce = AccessCensus.InForce(releaseId, "tv");

        var tableCodeByVid = AccessCensus.Rows(Accdb, $"SELECT tv.[TableVID], tv.[Code] FROM [TableVersion] tv WHERE {inForce}")
            .ToDictionary(r => Text(r[0]), r => Text(r[1]), StringComparer.Ordinal);
        var headerCode = new Dictionary<(string Vid, string HeaderId), string>();
        foreach (var r in AccessCensus.Rows(
                     Accdb,
                     "SELECT tvh.[TableVID], tvh.[HeaderID], hv.[Code] FROM ([TableVersionHeader] tvh INNER JOIN [HeaderVersion] hv ON hv.[HeaderVID] = tvh.[HeaderVID]) "
                     + $"INNER JOIN [TableVersion] tv ON tv.[TableVID] = tvh.[TableVID] WHERE {inForce}"))
        {
            headerCode[(Text(r[0]), Text(r[1]))] = Text(r[2]);
        }

        var result = new Dictionary<ShadingAgreement.Key, bool>();
        foreach (var r in AccessCensus.Rows(
                     Accdb,
                     "SELECT tvc.[TableVID], c.[RowID], c.[ColumnID], c.[SheetID], tvc.[IsExcluded] "
                     + "FROM ([TableVersionCell] tvc INNER JOIN [Cell] c ON c.[CellID] = tvc.[CellID]) "
                     + $"INNER JOIN [TableVersion] tv ON tv.[TableVID] = tvc.[TableVID] WHERE {inForce}"))
        {
            var vid = Text(r[0]);
            string? Code(object id) => id is DBNull ? null : headerCode.GetValueOrDefault((vid, Text(id)));
            var row = Code(r[1]);
            var column = Code(r[2]);
            if (row is null || column is null)
            {
                continue; // an open axis: no row/column code to compare (the layout grid has none either)
            }

            var key = new ShadingAgreement.Key(tableCodeByVid[vid], Code(r[3]), row, column);
            var enabled = !Convert.ToBoolean(r[4], CultureInfo.InvariantCulture);
            result[key] = result.TryGetValue(key, out var previous) ? previous || enabled : enabled;
        }

        return result;
    }

    [DataFact]
    public void LayoutShading_AgreesWithTheAccessCells_InBothDirections()
    {
        var layout = LayoutGrid();
        var access = AccessCells();
        Assert.NotEmpty(layout);
        Assert.NotEmpty(access);

        var raw = ShadingAgreement.Compare(layout, access);
        output.WriteLine("before the known exception: " + ShadingAgreement.Describe(raw));
        var excepted = ShadingAgreement.ApplyK1400HeaderCode10(raw);
        output.WriteLine($"known exception applied (K_14.00 header code '10' vs '0010', {ShadingAgreement.K1400HeaderCode10.Date}): {excepted.PairsExcused} pair(s). {ShadingAgreement.K1400HeaderCode10.Reason}");
        var result = excepted.Remaining;
        output.WriteLine(ShadingAgreement.Describe(result));
        Assert.True(excepted.StaleReasons.Count == 0, string.Join(Environment.NewLine, excepted.StaleReasons));
        foreach (var c in result.LayoutShadedAccessEnabled)
        {
            output.WriteLine($"layout shaded, Access enabled: {c.Sheet} row {c.RowCode} col {c.ColumnCode} ({c.CellRef})");
        }

        foreach (var c in result.LayoutEnabledAccessDisabledOrAbsent)
        {
            output.WriteLine($"layout enabled, Access excluded/absent: {c.Sheet} row {c.RowCode} col {c.ColumnCode} ({c.CellRef})");
        }

        foreach (var k in result.AccessEnabledNotInLayoutGrid)
        {
            output.WriteLine($"Access enabled, not in the layout grid: {k.TableCode} z={k.ZCode} row {k.RowCode} col {k.ColumnCode}");
        }

        // The universes are not empty on either side of each direction (a zero over nothing proves nothing).
        Assert.True(result.BothEnabled > 0 && result.BothDisabled > 0, ShadingAgreement.Describe(result));

        Assert.True(
            result.Disagreements == 0,
            ShadingAgreement.Describe(result) + Environment.NewLine
            + string.Join(Environment.NewLine, result.LayoutShadedAccessEnabled.Select(c => $"layout shaded, Access enabled: {c.Sheet} row {c.RowCode} col {c.ColumnCode} ({c.CellRef})"))
            + string.Join(Environment.NewLine, result.LayoutEnabledAccessDisabledOrAbsent.Select(c => $"layout enabled, Access excluded/absent: {c.Sheet} row {c.RowCode} col {c.ColumnCode} ({c.CellRef})"))
            + string.Join(Environment.NewLine, result.AccessEnabledNotInLayoutGrid.Select(k => $"Access enabled, not in the layout grid: {k.TableCode} z={k.ZCode} row {k.RowCode} col {k.ColumnCode}")));
    }

    /// <summary>
    /// Positive control of the shading DECISION: the colour of every grid cell is read from the workbook
    /// independently of the extractor, and every colour the extractor treats as "disabled" must be disabled
    /// in Access for ALL its cells, while the colours it leaves enabled must be enabled in Access. The census
    /// is printed per colour with both counts.
    /// </summary>
    [DataFact]
    public void EveryColourTheExtractorShades_IsDisabledInAccess_AndTheOthersAreEnabled_MeasuredPerColour()
    {
        var layout = LayoutGrid();
        var access = AccessCells();
        var fills = new FillReader();

        // colour -> (extractor shaded?, Access enabled count, Access disabled/absent count)
        var byColour = new SortedDictionary<string, (long Shaded, long Unshaded, long AccessEnabled, long AccessDisabled)>(StringComparer.Ordinal);
        var accessTables = access.Keys.Select(k => k.TableCode).ToHashSet(StringComparer.Ordinal);
        foreach (var cell in layout)
        {
            var key = new ShadingAgreement.Key(cell.TableCode, cell.ZCode, cell.RowCode, cell.ColumnCode);
            if (!accessTables.Contains(cell.TableCode))
            {
                continue; // the table is not matched in Access: counted by the agreement test, not by colour
            }

            var colour = fills.ColourOf(cell.File, cell.Sheet, cell.CellRef);
            var enabledInAccess = access.TryGetValue(key, out var e) && e;
            var (s, u, ae, ad) = byColour.GetValueOrDefault(colour);
            byColour[colour] = (s + (cell.IsShaded ? 1 : 0), u + (cell.IsShaded ? 0 : 1), ae + (enabledInAccess ? 1 : 0), ad + (enabledInAccess ? 0 : 1));
        }

        foreach (var (colour, c) in byColour)
        {
            output.WriteLine($"fill {colour}: extractor shaded {c.Shaded}, extractor enabled {c.Unshaded}; Access enabled {c.AccessEnabled}, Access excluded/absent {c.AccessDisabled}");
        }

        var problems = new List<string>();
        foreach (var (colour, c) in byColour)
        {
            if (c.Shaded > 0 && c.Unshaded > 0)
            {
                problems.Add($"fill {colour} is shaded for {c.Shaded} cells and not shaded for {c.Unshaded}: the decision is not a function of the colour");
            }

            if (c.Shaded > 0 && c.AccessEnabled > 0)
            {
                problems.Add($"fill {colour} is treated as shaded, but {c.AccessEnabled} of its cells are enabled in Access");
            }

            if (c.Shaded == 0 && c.Unshaded > 0 && c.AccessEnabled == 0)
            {
                problems.Add($"fill {colour} is NOT treated as shaded, but ALL its {c.Unshaded} cell(s) are excluded/absent in Access: a shading colour left out?");
            }
        }

        // The control is not vacuous: shaded colours and unshaded colours both exist in the real data.
        Assert.Contains(byColour, kv => kv.Value.Shaded > 0 && kv.Value.AccessEnabled == 0);
        Assert.Contains(byColour, kv => kv.Value.Unshaded > 0 && kv.Value.AccessEnabled > 0);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        // 595959 (dark grey, K_41.00 r0610 c0080) is a shading colour (decided 2026-11-20): shaded by the extractor, one cell, excluded in Access.
        var darkGrey = Assert.Single(byColour, kv => kv.Key.EndsWith("595959", StringComparison.OrdinalIgnoreCase));
        Assert.Equal((1L, 0L, 0L, 1L), darkGrey.Value);
        Assert.Contains(byColour, kv => kv.Key.EndsWith("999999", StringComparison.OrdinalIgnoreCase) && kv.Value.Shaded > 0 && kv.Value.Unshaded == 0);
    }

    /// <summary>
    /// The fill colour of a cell, from <c>xl/styles.xml</c> and the sheet XML (ZIP + XML, independent of
    /// <c>XlsxReader</c>): <c>none</c>, <c>indexed:N</c> or <c>rgb:AARRGGBB</c>, as written.
    /// </summary>
    private sealed class FillReader
    {
        private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace RIdNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> _cache = new(StringComparer.Ordinal);

        public string ColourOf(string file, string sheet, string cellRef)
        {
            if (!_cache.TryGetValue(file, out var sheets))
            {
                sheets = Read(file);
                _cache[file] = sheets;
            }

            return sheets[sheet].GetValueOrDefault(cellRef, "none");
        }

        private static Dictionary<string, Dictionary<string, string>> Read(string path)
        {
            using var zip = ZipFile.OpenRead(path);
            XDocument Load(string name)
            {
                using var s = zip.GetEntry(name)!.Open();
                return XDocument.Load(s);
            }

            var styles = Load("xl/styles.xml");
            var fills = styles.Root!.Element(Main + "fills")!.Elements(Main + "fill").Select(f =>
            {
                var p = f.Element(Main + "patternFill");
                if ((string?)p?.Attribute("patternType") != "solid")
                {
                    return "none";
                }

                var fg = p!.Element(Main + "fgColor");
                if (fg?.Attribute("indexed") is { } idx)
                {
                    return "indexed:" + idx.Value;
                }

                return fg?.Attribute("rgb") is { } rgb ? "rgb:" + rgb.Value : "solid";
            }).ToList();
            var xfFill = styles.Root.Element(Main + "cellXfs")!.Elements(Main + "xf").Select(x => (int?)x.Attribute("fillId") ?? 0).ToList();

            var workbook = Load("xl/workbook.xml");
            var rels = Load("xl/_rels/workbook.xml.rels");
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var sheet in workbook.Descendants(Main + "sheet"))
            {
                var name = (string)sheet.Attribute("name")!;
                var rId = (string)sheet.Attribute(RIdNs + "id")!;
                var target = (string)rels.Descendants(RelNs + "Relationship").Single(r => (string)r.Attribute("Id")! == rId).Attribute("Target")!;
                var part = "xl/" + target.TrimStart('/').Replace("xl/", string.Empty, StringComparison.Ordinal);
                var cells = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var c in Load(part).Descendants(Main + "c"))
                {
                    var style = (int?)c.Attribute("s") ?? 0;
                    cells[(string)c.Attribute("r")!] = fills[xfFill[style]];
                }

                result[name] = cells;
            }

            return result;
        }
    }
}
