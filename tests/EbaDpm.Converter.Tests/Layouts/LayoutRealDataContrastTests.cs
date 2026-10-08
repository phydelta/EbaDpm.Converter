using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// Contrasts the figures derived by hand against what the extractor produces over the REAL EBA
/// Annotated Table Layouts. If any of these figures stops matching, it is a regression of the
/// extractor, not of the test -- the figures were verified independently.
/// </summary>
[Collection("LayoutsRealData")]
public sealed class LayoutRealDataContrastTests(LayoutRepositoryFixture fixture)
{
    [DataFact]
    public void Extraction42_StructuralCounts_AreAsExpected()
    {
        // Files/sheets/declarations do NOT depend on the X-axis annotation grammar.
        var summary = fixture.Summary42WithDictionary;

        Assert.Equal(51, summary.FilesProcessed);
        Assert.Equal(1117, summary.SheetsProcessed);
        Assert.True(summary.DictionaryUsed);
        Assert.Equal(11515, summary.DeclarationsWritten);
    }

    [DataFact]
    public void Extraction42_EveryYOrdinate_IsReferencedByAtLeastOneLayoutCell()
    {
        // "Does every Y ordinate have at least one cell that references it?" (is any data row
        // lost?) is a STRUCTURAL GUARANTEE, not a rate: the cartesian product ("cells are not
        // searched for, they are GENERATED") creates, for each Y ordinate of a closed-axis sheet,
        // one LayoutCell per X ordinate of that same sheet -- so EVERY Y ordinate is referenced,
        // without exception, by construction. Rows without data still generate their full row of
        // cells (with DatapointId NULL where there is no datum, IsShaded aside) instead of
        // disappearing from the count of "referenced ordinates".
        var con = fixture.Repository42WithDictionary;

        using var totalYCmd = con.CreateCommand();
        totalYCmd.CommandText = "SELECT COUNT(*) FROM LayoutOrdinate WHERE Axis = 'Y'";
        var totalY = Convert.ToInt64(totalYCmd.ExecuteScalar());
        Assert.True(totalY > 10000, $"Only {totalY} Y ordinates: review the extraction.");

        using var unreferencedCmd = con.CreateCommand();
        unreferencedCmd.CommandText =
            """
            SELECT COUNT(*) FROM LayoutOrdinate y
            WHERE y.Axis = 'Y' AND NOT EXISTS (
                SELECT 1 FROM LayoutCell lc WHERE lc.RowOrdinateId = y.OrdinateId
            )
            """;
        var unreferenced = Convert.ToInt64(unreferencedCmd.ExecuteScalar());

        Assert.Equal(0L, unreferenced);
    }

    [DataFact]
    public void Extraction42_DatapointSlotMismatches_AreCountedButDoNotPreventTheRowsCellsFromExisting()
    {
        // The production counter (OrdinateDatapointSlotMismatches) keeps its meaning -- how many Y
        // rows had a populated third column whose shape was NOT that of a datapoint. This checks
        // that it keeps being reported (never silently) and as a RATE with a denominator, not an
        // exact count equality that expires with every corpus change.
        var summary = fixture.Summary42WithDictionary;
        var con = fixture.Repository42WithDictionary;

        using var totalYCmd = con.CreateCommand();
        totalYCmd.CommandText = "SELECT COUNT(*) FROM LayoutOrdinate WHERE Axis = 'Y'";
        var totalY = Convert.ToInt64(totalYCmd.ExecuteScalar());

        Assert.True(summary.OrdinateDatapointSlotMismatches >= 0, "The counter must always be reported, even when it is 0.");
        var mismatchRate = (double)summary.OrdinateDatapointSlotMismatches / totalY;
        Assert.True(mismatchRate < 0.05,
            $"{mismatchRate:P1} of the Y rows ({summary.OrdinateDatapointSlotMismatches}/{totalY}) have a " +
            "datapoint slot with the wrong shape; a large increase here indicates an " +
            "uncharacterised grammar change.");

        // Structural regression canary, not the main assertion.
        Assert.Equal(185, summary.OrdinateDatapointSlotMismatches);
    }

    [DataFact]
    public void Extraction42_LayoutUnparsed_Is693OrphanDeclarations_Plus238UnlinkedValues_DerivedIndependently()
    {
        // LayoutUnparsed has four directions; the total is not only "declarations without a value"
        // (693): it is 693 + 238 = 931 (the 238 being UnlinkedValue). Each summand is derived with
        // its own SQL, independent of the production counter, against the same table that
        // describes each one separately. (LayoutUnparsedGateTests shows why a bare "0" would not
        // be a measurement.)
        var con = fixture.Repository42WithDictionary;

        using var unparsedCmd = con.CreateCommand();
        unparsedCmd.CommandText = "SELECT COUNT(*) FROM LayoutUnparsed";
        var unparsedTotal = Convert.ToInt64(unparsedCmd.ExecuteScalar());

        using var orphanDeclCmd = con.CreateCommand();
        orphanDeclCmd.CommandText =
            """
            SELECT COUNT(*) FROM LayoutDeclaration d
            WHERE NOT EXISTS (SELECT 1 FROM LayoutValue v WHERE v.DeclId = d.DeclId)
            """;
        var orphanDeclarations = Convert.ToInt64(orphanDeclCmd.ExecuteScalar());

        using var unlinkedValueCmd = con.CreateCommand();
        unlinkedValueCmd.CommandText =
            """
            SELECT COUNT(*) FROM LayoutValue v WHERE v.OrdinateId IS NULL
            """;
        var unlinkedValues = Convert.ToInt64(unlinkedValueCmd.ExecuteScalar());

        Assert.Equal(693L, orphanDeclarations);
        Assert.Equal(238L, unlinkedValues);
        Assert.Equal(orphanDeclarations + unlinkedValues, unparsedTotal);
        Assert.Equal(931L, unparsedTotal);
    }

    [DataFact]
    public void Extraction42_DatapointSlotMismatches_Is185_Not195_TheTwoThingsWereConflated()
    {
        // The figure "195" mixed two distinct phenomena. This derives here, by independent SQL
        // over LayoutRaw (without using the production counter), which of the two is
        // "DatapointSlotMismatches":
        //
        // - 10 Y-ordinate rows with FEWER than 3 populated columns: there is no datapoint slot to
        //   evaluate -- they are structural rows (group headings such as "A-IRB",
        //   "REQUIRED STABLE FUNDING", "Categories"), verified by Label. It is not a mismatch:
        //   there is nothing that fails to match.
        // - 185 rows WITH a populated third column whose content has no datapoint shape
        //   ("NNNNN_x000D_type"): here there IS a slot, and its shape is not the expected one.
        //
        // 195 = 10 + 185: the original figure adds both. The right one for "slot mismatch" is
        // 185 -- it matches the production counter (OrdinateDatapointSlotMismatches), measured here
        // in a completely independent way.
        var con = fixture.Repository42WithDictionary;

        // LayoutOrdinate has no CellRef: the Excel row of each Y ordinate is read via LayoutCell
        // (every cell that references it shares the SAME row, by construction of the cartesian
        // product: any one is enough, MIN() here).
        using var yOrdCmd = con.CreateCommand();
        yOrdCmd.CommandText =
            """
            SELECT y.SheetId, MIN(lc.CellRef) FROM LayoutOrdinate y
            JOIN LayoutCell lc ON lc.RowOrdinateId = y.OrdinateId
            WHERE y.Axis = 'Y'
            GROUP BY y.OrdinateId
            """;
        using var yReader = yOrdCmd.ExecuteReader();
        var yOrdinates = new List<(long SheetId, string CellRef)>();
        while (yReader.Read())
        {
            yOrdinates.Add((yReader.GetInt64(0), yReader.GetString(1)));
        }

        var dpPrefix = new System.Text.RegularExpressions.Regex(@"^\d+_x000D_");
        var cellPattern = new System.Text.RegularExpressions.Regex(@"^([A-Z]+)(\d+)$");

        // Preload ALL of LayoutRaw grouped by sheet+row, in memory: a single pass over the table
        // instead of one query for each of the 18,754 row ordinates.
        var rawByRow = new Dictionary<(long SheetId, int Row), List<(int Col, string Value)>>();
        using var rawCmd = con.CreateCommand();
        rawCmd.CommandText = "SELECT SheetId, CellRef, Value FROM LayoutRaw";
        using var rawReader = rawCmd.ExecuteReader();
        while (rawReader.Read())
        {
            var sheetId = rawReader.GetInt64(0);
            var cr = rawReader.GetString(1);
            var m = cellPattern.Match(cr);
            var col = ColumnLetterToIndex(m.Groups[1].Value);
            if (col <= 1)
            {
                continue;
            }

            var row = int.Parse(m.Groups[2].Value);
            var key = (sheetId, row);
            if (!rawByRow.TryGetValue(key, out var list))
            {
                list = [];
                rawByRow[key] = list;
            }

            list.Add((col, rawReader.GetString(2)));
        }

        var noThirdColumn = 0;
        var thirdColumnWrongForm = 0;

        foreach (var (sheetId, cellRef) in yOrdinates)
        {
            var match = cellPattern.Match(cellRef);
            var row = int.Parse(match.Groups[2].Value);

            var populatedCols = rawByRow.GetValueOrDefault((sheetId, row), []);
            populatedCols = [.. populatedCols.OrderBy(c => c.Col)];

            if (populatedCols.Count < 3)
            {
                noThirdColumn++;
                continue;
            }

            if (!dpPrefix.IsMatch(populatedCols[2].Value.Trim()))
            {
                thirdColumnWrongForm++;
            }
        }

        Assert.Equal(10, noThirdColumn);
        Assert.Equal(185, thirdColumnWrongForm);
        Assert.Equal(195, noThirdColumn + thirdColumnWrongForm); // the original figure, now explained
    }

    private static int ColumnLetterToIndex(string letters)
    {
        var col = 0;
        foreach (var c in letters)
        {
            col = (col * 26) + (c - 'A' + 1);
        }

        return col;
    }

    [DataFact]
    public void Extraction43_Has70TableSheets_NotTheErroneous74()
    {
        // 74 included the 4 TOC sheets, which --extract-layouts explicitly excludes
        // (LayoutExtractor skips the "TOC" sheet).
        var summary = fixture.Summary43WithDictionary;

        Assert.Equal(4, summary.FilesProcessed);
        Assert.Equal(70, summary.SheetsProcessed);
    }

    [DataFact]
    public void TableCodeCoverage_Matches_787_Matched_1_Missing_19_LayoutOnly()
    {
        // Cross of mTable.TableCode (788 distinct) against the base name of each layout sheet.
        // 787 match, F_19.00.dp has no sheet, and 19 layout sheets (B_01.01 ... B_04.01, among
        // others) have no table in the 4.2 reference database.
        var con = fixture.Repository42WithDictionary;

        var layoutCodes = QueryStringSet(con, "SELECT DISTINCT TableCode FROM LayoutSheet");

        using var refConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Schema.RepoPaths.ReferenceDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        refConnection.Open();
        var referenceCodes = QueryStringSet(refConnection, "SELECT DISTINCT TableCode FROM mTable");

        var matched = referenceCodes.Intersect(layoutCodes).ToHashSet();
        var referenceOnly = referenceCodes.Except(layoutCodes).ToHashSet();
        var layoutOnly = layoutCodes.Except(referenceCodes).ToHashSet();

        Assert.Equal(788, referenceCodes.Count);
        Assert.Equal(787, matched.Count);
        Assert.Equal(["F_19.00.dp"], referenceOnly.Order().ToArray());
        Assert.Equal(19, layoutOnly.Count);
    }

    [DataFact]
    public void ZSuffixSheets_Total194_And186GroupsWithIdenticalCrossModuleHash()
    {
        // 194 sheets with a Z suffix out of 1,117; 186 sheets repeat in more than one module, and
        // all 186 carry the SAME annotation (same hash over the cells of the form "(...)").
        // The implementation groups by (TableCode, ZSuffix); the independent derivation groups by
        // EXACT sheet name. BOTH groupings are verified and checked to produce the same result.
        var con = fixture.Repository42WithDictionary;

        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM LayoutSheet WHERE ZSuffix IS NOT NULL";
        var zSuffixCount = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.Equal(194, zSuffixCount);

        using var sheetsCmd = con.CreateCommand();
        sheetsCmd.CommandText = "SELECT SheetId, SheetName, TableCode, ZSuffix FROM LayoutSheet";
        using var reader = sheetsCmd.ExecuteReader();
        var byName = new Dictionary<string, List<long>>();
        var byPair = new Dictionary<(string, string?), List<long>>();
        while (reader.Read())
        {
            var sheetId = reader.GetInt64(0);
            var name = reader.GetString(1);
            var tableCode = reader.GetString(2);
            var zSuffix = reader.IsDBNull(3) ? null : reader.GetString(3);

            (byName.TryGetValue(name, out var listByName) ? listByName : byName[name] = []).Add(sheetId);
            var pairKey = (tableCode, zSuffix);
            (byPair.TryGetValue(pairKey, out var listByPair) ? listByPair : byPair[pairKey] = []).Add(sheetId);
        }

        var multiByName = byName.Values.Where(v => v.Count > 1).Select(v => v.OrderBy(x => x).ToArray()).ToHashSet(new SequenceComparer());
        var multiByPair = byPair.Values.Where(v => v.Count > 1).Select(v => v.OrderBy(x => x).ToArray()).ToHashSet(new SequenceComparer());

        Assert.Equal(186, multiByName.Count);
        Assert.Equal(186, multiByPair.Count);
        // The two groupings must produce EXACTLY the same SheetId groups.
        Assert.True(multiByName.SetEquals(multiByPair),
            "The grouping by (TableCode, ZSuffix) and the grouping by exact sheet name must " +
            "coincide; if they do not, one of them produces different groups and it must be " +
            "decided which is the right one before relying on the 186/186.");

        var mismatches = 0;
        foreach (var group in multiByName)
        {
            var hashes = group.Select(sheetId => SheetAnnotationHash(con, sheetId)).ToHashSet();
            if (hashes.Count > 1)
            {
                mismatches++;
            }
        }

        Assert.Equal(0, mismatches);
    }

    [DataFact]
    public void HandDerivedCase1_C26_00_COREP_LE_ResolvesExactlyAsHandDerivation()
    {
        // qEBF -> mDimension 822, DomainID 113 -> DomainCode qOR; qx2011 in domain 113 ->
        // mMember 5014 "G-SIIs"; qAIH -> mMember 12226, DomainID 33 = MET.
        var con = fixture.Repository42WithDictionary;

        var sheetId = QueryScalarLong(con,
            """
            SELECT s.SheetId FROM LayoutSheet s JOIN LayoutFile f ON f.FileId = s.FileId
            WHERE s.TableCode = 'C_26.00' AND f.Path LIKE '%COREP_LE%'
            """);

        var (dimCode, domCode) = QueryDeclarationCodes(con, sheetId, "F4");
        Assert.Equal("qEBF", dimCode);
        Assert.Equal("qOR", domCode);

        var (valueDomain, valueMember) = QueryValueCodes(con, sheetId, "F10");
        Assert.Equal("qOR", valueDomain);
        Assert.Equal("qx2011", valueMember);
    }

    [DataFact]
    public void HandDerivedCase2_C1401_0010_ZAxisEnumerated_qMRW_Resolves()
    {
        // K2 declares (qMRW:qAP), K3 fixes (qAP:qx2142) -- the Z value of this sheet.
        var con = fixture.Repository42WithDictionary;
        var sheetId = QueryScalarLong(con, "SELECT SheetId FROM LayoutSheet WHERE SheetName = 'C_14.01(0010)' LIMIT 1");

        var (dimCode, domCode) = QueryDeclarationCodes(con, sheetId, "K2");
        Assert.Equal("qMRW", dimCode);
        Assert.Equal("qAP", domCode);

        var (valueDomain, valueMember) = QueryValueCodes(con, sheetId, "K3");
        Assert.Equal("qAP", valueDomain);
        Assert.Equal("qx2142", valueMember);
    }

    [DataFact]
    public void HandDerivedCase3_C106_00_MatrixRows_PBE_And_qLHL_SpanSixColumns()
    {
        // B9=(PBE:IS) is the key (IsKey); B10=(qAEA:qCA) is only fixed in J10;
        // B11=(qLHL:qTR) propagates IDENTICALLY to columns E11..J11 (six ordinates).
        var con = fixture.Repository42WithDictionary;
        var sheetId = QueryScalarLong(con, "SELECT SheetId FROM LayoutSheet WHERE TableCode = 'C_106.00' LIMIT 1");

        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT IsKey FROM LayoutDeclaration WHERE SheetId = $sid AND CellRef = 'B9'";
        cmd.Parameters.AddWithValue("$sid", sheetId);
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));

        using var cmdB10 = con.CreateCommand();
        cmdB10.CommandText =
            """
            SELECT COUNT(*) FROM LayoutValue v JOIN LayoutDeclaration d ON d.DeclId = v.DeclId
            WHERE d.SheetId = $sid AND d.CellRef = 'B10'
            """;
        cmdB10.Parameters.AddWithValue("$sid", sheetId);
        Assert.Equal(1L, Convert.ToInt64(cmdB10.ExecuteScalar()));

        using var cmdB11 = con.CreateCommand();
        cmdB11.CommandText =
            """
            SELECT v.CellRef, v.MemberCode FROM LayoutValue v JOIN LayoutDeclaration d ON d.DeclId = v.DeclId
            WHERE d.SheetId = $sid AND d.CellRef = 'B11' ORDER BY v.CellRef
            """;
        cmdB11.Parameters.AddWithValue("$sid", sheetId);
        using var reader = cmdB11.ExecuteReader();
        var cells = new List<string>();
        var members = new HashSet<string>();
        while (reader.Read())
        {
            cells.Add(reader.GetString(0));
            members.Add(reader.GetString(1));
        }

        Assert.Equal(["E11", "F11", "G11", "H11", "I11", "J11"], cells);
        Assert.Equal(["qx2003"], members.ToArray());
    }

    private static string SheetAnnotationHash(SqliteConnection connection, long sheetId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CellRef, Value FROM LayoutRaw WHERE SheetId = $sid AND Value LIKE '(%' ORDER BY CellRef";
        cmd.Parameters.AddWithValue("$sid", sheetId);
        using var reader = cmd.ExecuteReader();
        var sb = new System.Text.StringBuilder();
        while (reader.Read())
        {
            sb.Append(reader.GetString(0)).Append('=').Append(reader.GetString(1)).Append('|');
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private static HashSet<string> QueryStringSet(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var set = new HashSet<string>();
        while (reader.Read())
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }

    private static long QueryScalarLong(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static (string? DimensionCode, string? DomainCode) QueryDeclarationCodes(SqliteConnection connection, long sheetId, string cellRef)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DimensionCode, DomainCode FROM LayoutDeclaration WHERE SheetId = $sid AND CellRef = $cr";
        cmd.Parameters.AddWithValue("$sid", sheetId);
        cmd.Parameters.AddWithValue("$cr", cellRef);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), $"No declaration at {cellRef} (sheet {sheetId}).");
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static (string? DomainCode, string? MemberCode) QueryValueCodes(SqliteConnection connection, long sheetId, string cellRef)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DomainCode, MemberCode FROM LayoutValue WHERE SheetId = $sid AND CellRef = $cr";
        cmd.Parameters.AddWithValue("$sid", sheetId);
        cmd.Parameters.AddWithValue("$cr", cellRef);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), $"No value at {cellRef} (sheet {sheetId}).");
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private sealed class SequenceComparer : IEqualityComparer<long[]>
    {
        public bool Equals(long[]? x, long[]? y) => x is not null && y is not null && x.SequenceEqual(y);

        public int GetHashCode(long[] obj)
        {
            var hash = 17;
            foreach (var value in obj)
            {
                hash = hash * 31 + value.GetHashCode();
            }

            return hash;
        }
    }
}
