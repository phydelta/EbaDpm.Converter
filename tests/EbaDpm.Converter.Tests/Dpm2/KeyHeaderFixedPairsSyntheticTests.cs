using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Mapping.Dpm20;
using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Tests.Dictionary;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Synthetic coverage of the projection of a KEY header's fixed pairs onto its open-axis
/// ordinate (issue #9): <c>BuildTablePlan</c> reads <c>keyHeader.ContextId</c>, resolves it with
/// <c>ResolveContextOwnPairs</c> into <c>OpenAxisPlan.FixedPairs</c>, and <c>EmitTablePlan</c>
/// writes them to <c>mOrdinateCategorisation</c> next to the open pair (<c>MemberID</c> 9999).
/// Runs <c>Dpm20AxisAndCellLoader.Load</c> from in-memory rows
/// (<see cref="InMemoryAxisAndCellSource"/>) over a destination built with the real schema; no
/// data files needed. The real-data counterpart (negative case on C_08.05) is
/// <see cref="KeyHeaderFixedPairsRealDataTests"/>.
///
/// Fixture: ONE table T_01 with a closed X axis (header 0010, a metric) and a key header 0020 of
/// direction X (transposed to an open Y axis) whose property is the open dimension <c>dOpen</c>.
/// Context 5000 has two (property, item) pairs: <c>dA = a1</c> and <c>dB = b1</c>.
/// </summary>
public sealed class KeyHeaderFixedPairsSyntheticTests
{
    private const int KeyContextId = 5000;
    private const int OpenAxisMemberSentinel = 9999;

    [Fact]
    public void KeyHeaderContextId_ProjectsBothFixedPairsOntoTheOpenOrdinate_NextToTheOpenPair()
    {
        var pairs = LoadAndReadOpenOrdinateCategorisation(keyHeaderContextId: KeyContextId);

        // The open pair (MemberID 9999) plus the two fixed pairs of the context: 3 rows.
        Assert.Equal(["dA=a1", "dB=b1", "dOpen=<open>"], pairs);
    }

    [Fact]
    public void NegativeControl_KeyHeaderWithoutContextId_ProjectsOnlyTheOpenPair()
    {
        var pairs = LoadAndReadOpenOrdinateCategorisation(keyHeaderContextId: null);

        // Same fixture, ContextID NULL: 1 row against 3.
        Assert.Equal(["dOpen=<open>"], pairs);
    }

    /// <summary>
    /// The pairs of the open-axis ordinate (code <c>0020</c>) as sorted
    /// <c>DimensionCode=MemberCode</c> business keys; the sentinel member prints as
    /// <c>&lt;open&gt;</c> (the sentinel member of the sentinel domain, as the dictionary loader writes it).
    /// </summary>
    private static List<string> LoadAndReadOpenOrdinateCategorisation(int? keyHeaderContextId)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"EbaDpm.KeyHeaderFixedPairs_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "synthetic.db");
        try
        {
            SchemaCreator.Create(path, overwrite: false);

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            connection.Open();
            SeedDestinationDictionary(connection);

            var source = BuildSource(keyHeaderContextId);
            var tableVersions = new List<Dpm20TableVersionRow>
            {
                new(TableVId: 1, Code: "T_01", Name: "Synthetic table", TableId: 1, AbstractTableId: null,
                    StartReleaseId: 1, EndReleaseId: null, IsAbstract: false, ContextId: null),
            };

            var result = Dpm20AxisAndCellLoader.Load(
                source, connection,
                new Dictionary<int, int> { [1] = 1 },
                tableVersions,
                new Dictionary<int, (int HierarchyId, string HierarchyCode, int DomainId)>());

            // The fixture must be a clean run: the only admissible anomaly is the loader's own
            // note that the key header contributes fixed pairs.
            Assert.Equal(0, result.UnresolvedContextPairs);
            Assert.Equal(0, result.UnresolvedOpenAxisDimensions);
            Assert.All(
                result.StructuralAnomalies,
                a => Assert.Contains("contributes 2 FIXED pair(s)", a));

            // Exactly one open-axis ordinate, and it is the one we assert on.
            Assert.Equal(
                1,
                QueryHelpers.Scalar(
                    connection,
                    "SELECT COUNT(*) FROM \"mAxisOrdinate\" o JOIN \"mAxis\" a ON a.\"AxisID\" = o.\"AxisID\" WHERE a.\"IsOpenAxis\" = 1 AND o.\"OrdinateCode\" = '0020'"));

            var rows = QueryHelpers.Rows(
                connection,
                $"""
                SELECT d."DimensionCode",
                       CASE WHEN c."MemberID" = {OpenAxisMemberSentinel} THEN '<open>' ELSE m."MemberCode" END
                FROM "mOrdinateCategorisation" c
                JOIN "mAxisOrdinate" o ON o."OrdinateID" = c."OrdinateID"
                JOIN "mAxis" a ON a."AxisID" = o."AxisID" AND a."IsOpenAxis" = 1
                JOIN "mDimension" d ON d."DimensionID" = c."DimensionID"
                LEFT JOIN "mMember" m ON m."MemberID" = c."MemberID"
                WHERE o."OrdinateCode" = '0020'
                """,
                2);

            return rows.Select(r => $"{r[0]}={r[1]}").OrderBy(s => s, StringComparer.Ordinal).ToList();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }

    /// <summary>The dictionary the dictionary loader would have written, reduced to what the fixture uses.</summary>
    private static void SeedDestinationDictionary(SqliteConnection connection)
    {
        Execute(
            connection,
            """
            INSERT INTO "mTable" ("TableID", "TableCode") VALUES (1, 'T_01');

            INSERT INTO "mDomain" ("DomainID", "DomainCode") VALUES
                (1, 'MET'), (2, 'INT_NA'), (3, 'STR_NA'), (4, 'DA'), (5, 'DB'), (6, 'DO'), (9999, '');

            INSERT INTO "mMember" ("MemberID", "DomainID", "MemberCode", "MemberXBRLCode", "IsDefaultMember") VALUES
                (1, 1, 'met1', 'eba_met1', 0),
                (2, 4, 'a1',   'eba_a1',   0),
                (3, 5, 'b1',   'eba_b1',   0),
                (9999, 9999, '', NULL, 0);

            INSERT INTO "mDimension" ("DimensionID", "DimensionCode", "DimensionXBRLCode", "DomainID") VALUES
                (10, 'dOpen', 'eba_dOpen', 6),
                (11, 'dA',    'eba_dA',    4),
                (12, 'dB',    'eba_dB',    5),
                (9999, 'MET', 'MET', 1);
            """);
    }

    private static InMemoryAxisAndCellSource BuildSource(int? keyHeaderContextId)
    {
        const int pr = 1, na = 2, catMet = 3, catDa = 4, catDb = 5, catDo = 6;

        var source = new InMemoryAxisAndCellSource { CutoffReleaseId = 5 };

        source.Categories.AddRange(
        [
            new(pr, "_PR", null, null, false), new(na, "_NA", null, null, false), new(catMet, "MET", null, null, true),
            new(catDa, "DA", null, null, true), new(catDb, "DB", null, null, true), new(catDo, "DO", null, null, false),
        ]);

        // _PR windows: the CODE of each property (ItemID = PropertyID). 103 is the metric.
        source.ItemCategories.AddRange(
        [
            new(100, pr, "dOpen", false, 1, null), new(101, pr, "dA", false, 1, null),
            new(102, pr, "dB", false, 1, null), new(103, pr, "met1", false, 1, null),
            new(201, catDa, "a1", false, 1, null), new(202, catDb, "b1", false, 1, null),
        ]);

        // The domain of the VALUES of each dimension property.
        source.PropertyCategories.AddRange(
        [
            new(100, catDo, 1, null), new(101, catDa, 1, null), new(102, catDb, 1, null), new(103, catMet, 1, null),
        ]);

        // Closed X axis: header 10 (the metric). Key header 11 of direction X: the open axis.
        source.Headers.AddRange([new(10, 1, "X", false), new(11, 1, "X", true)]);
        source.HeaderVersions.AddRange(
        [
            new(HeaderVId: 1010, HeaderId: 10, Code: "0010", Label: "Metric column", PropertyId: 103, ContextId: null, SubCategoryVId: null),
            new(HeaderVId: 1011, HeaderId: 11, Code: "0020", Label: "Open key", PropertyId: 100, ContextId: keyHeaderContextId, SubCategoryVId: null),
        ]);
        source.TableVersionHeaders.AddRange(
        [
            new(TableVId: 1, HeaderId: 10, HeaderVId: 1010, ParentHeaderId: null, Order: 1, IsAbstract: false),
            new(TableVId: 1, HeaderId: 11, HeaderVId: 1011, ParentHeaderId: null, Order: 2, IsAbstract: false),
        ]);

        // One live cell on the closed column.
        source.Cells.Add(new(CellId: 1, TableId: 1, ColumnId: 10, RowId: null, SheetId: null));
        source.TableVersionCells.Add(new(TableVId: 1, CellId: 1, IsExcluded: false, IsVoid: false, VariableVId: null));

        // The key header's context: two (property, item) pairs, dA = a1 and dB = b1.
        source.ContextCompositions.AddRange(
        [
            new(KeyContextId, PropertyId: 101, ItemId: 201),
            new(KeyContextId, PropertyId: 102, ItemId: 202),
        ]);

        return source;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
