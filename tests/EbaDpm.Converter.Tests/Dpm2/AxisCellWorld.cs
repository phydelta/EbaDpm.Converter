using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Mapping.Dpm20;
using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Tests.Dictionary;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Small builder of a synthetic DPM 2.0 world for <c>Dpm20AxisAndCellLoader.Load</c>: ONE table
/// (<c>TableID</c> 1, <c>TableVID</c> 1, code <c>T_01</c>), a dictionary seeded in the destination
/// exactly as the dictionary loader would have written it, and the in-memory source rows. A test
/// declares dimensions, metrics, contexts, headers and cells, then <see cref="Run"/>s the loader
/// over a real-schema SQLite destination. No data files needed.
///
/// Conventions of the fixture: a dimension <c>dX</c> has the XBRL code <c>eba_dX</c> (unless given)
/// and its members <c>m</c> have the XBRL code <c>eba_m</c>; a metric <c>met1</c> is the member
/// <c>met1</c> (XBRL <c>eba_met1</c>) of the <c>MET</c> domain.
/// </summary>
internal sealed class AxisCellWorld
{
    public const int OpenSentinel = 9999;

    private const int PrCategory = 1;
    private const int NaCategory = 2;
    private const int MetCategory = 3;

    private readonly List<string> _destinationSql = [];
    private int _nextCategory = 10;
    private int _nextItem = 1000; // ItemID == PropertyID in _PR, so one counter for both.
    private int _nextDomain = 10;
    private int _nextDimension = 10;
    private int _nextMember = 10;
    private int _nextContext = 5000;
    private int _nextVariable = 1;
    private int _nextHeader = 10;
    private int _nextOrder = 1;
    private int _nextCell = 1;

    public AxisCellWorld()
    {
        Source = new InMemoryAxisAndCellSource { CutoffReleaseId = 5 };
        Source.Categories.AddRange(
        [
            new(PrCategory, "_PR", null, null, false),
            new(NaCategory, "_NA", null, null, false),
            new(MetCategory, "MET", null, null, true),
        ]);

        _destinationSql.Add(
            """
            INSERT INTO "mTable" ("TableID", "TableCode") VALUES (1, 'T_01');
            INSERT INTO "mDomain" ("DomainID", "DomainCode") VALUES (1, 'MET'), (2, 'INT_NA'), (3, 'STR_NA'), (9999, '');
            INSERT INTO "mMember" ("MemberID", "DomainID", "MemberCode", "MemberXBRLCode", "IsDefaultMember") VALUES (9999, 9999, '', NULL, 0);
            INSERT INTO "mDimension" ("DimensionID", "DimensionCode", "DimensionXBRLCode", "DomainID") VALUES (9999, 'MET', 'MET', 1);
            """);
    }

    public InMemoryAxisAndCellSource Source { get; }

    /// <summary><c>TableVersion.ContextID</c> of the table.</summary>
    public int? TableContextId { get; set; }

    /// <summary>A dimension of the dictionary: its property and the item of each member code.</summary>
    public sealed record Dimension(string Code, string XbrlCode, int PropertyId, IReadOnlyDictionary<string, int> ItemIdByMember);

    /// <summary>Declares a dimension with its domain and members; <paramref name="defaultMember"/> is flagged <c>IsDefaultMember</c>.</summary>
    public Dimension AddDimension(string code, string[] members, string? defaultMember = null, string? xbrlCode = null)
    {
        var categoryId = _nextCategory++;
        var domainId = _nextDomain++;
        var dimensionId = _nextDimension++;
        var propertyId = _nextItem++;
        xbrlCode ??= "eba_" + code;

        Source.Categories.Add(new(categoryId, "D_" + code, null, null, true));
        Source.ItemCategories.Add(new(propertyId, PrCategory, code, false, 1, null));
        Source.PropertyCategories.Add(new(propertyId, categoryId, 1, null));

        _destinationSql.Add(
            $"""
            INSERT INTO "mDomain" ("DomainID", "DomainCode") VALUES ({domainId}, 'D_{code}');
            INSERT INTO "mDimension" ("DimensionID", "DimensionCode", "DimensionXBRLCode", "DomainID") VALUES ({dimensionId}, '{code}', '{xbrlCode}', {domainId});
            """);

        var items = new Dictionary<string, int>();
        foreach (var member in members)
        {
            var itemId = _nextItem++;
            items[member] = itemId;
            Source.ItemCategories.Add(new(itemId, categoryId, member, false, 1, null));
            _destinationSql.Add(
                $"""
                INSERT INTO "mMember" ("MemberID", "DomainID", "MemberCode", "MemberXBRLCode", "IsDefaultMember")
                VALUES ({_nextMember++}, {domainId}, '{member}', 'eba_{member}', {(member == defaultMember ? 1 : 0)});
                """);
        }

        return new Dimension(code, xbrlCode, propertyId, items);
    }

    /// <summary>Declares a metric: the member <paramref name="code"/> of the <c>MET</c> domain, reached through a property. Returns the <c>PropertyID</c>.</summary>
    public int AddMetric(string code, bool isDefault = false)
    {
        var propertyId = _nextItem++;
        Source.ItemCategories.Add(new(propertyId, PrCategory, code, false, 1, null));
        Source.PropertyCategories.Add(new(propertyId, MetCategory, 1, null));
        _destinationSql.Add(
            $"""
            INSERT INTO "mMember" ("MemberID", "DomainID", "MemberCode", "MemberXBRLCode", "IsDefaultMember")
            VALUES ({_nextMember++}, 1, '{code}', 'eba_{code}', {(isDefault ? 1 : 0)});
            """);
        return propertyId;
    }

    /// <summary>A <c>ContextID</c> whose composition is the given (dimension, member) pairs.</summary>
    public int AddContext(params (Dimension Dimension, string Member)[] pairs)
    {
        var contextId = _nextContext++;
        foreach (var (dimension, member) in pairs)
        {
            Source.ContextCompositions.Add(new(contextId, dimension.PropertyId, dimension.ItemIdByMember[member]));
        }

        return contextId;
    }

    /// <summary>
    /// Adds a header (<c>Header</c> + <c>HeaderVersion</c> + <c>TableVersionHeader</c>) in source
    /// order of declaration. For a non-key header <paramref name="propertyId"/> is its metric; for
    /// a key header it is the open dimension's property. Returns the <c>HeaderID</c>.
    /// </summary>
    public int AddHeader(
        string direction, string? code, string? label = null, bool isKey = false, bool isAbstract = false,
        int? parentHeaderId = null, int? propertyId = null, int? contextId = null, int? subCategoryVId = null)
    {
        var headerId = _nextHeader++;
        var headerVId = 1000 + headerId;
        Source.Headers.Add(new(headerId, 1, direction, isKey));
        Source.HeaderVersions.Add(new(headerVId, headerId, code, label ?? "label " + code, propertyId, contextId, subCategoryVId));
        Source.TableVersionHeaders.Add(new(TableVId: 1, headerId, headerVId, parentHeaderId, _nextOrder++, isAbstract));
        return headerId;
    }

    /// <summary>
    /// Adds a source cell at the coordinate (HeaderIDs of its closed ordinates) with its
    /// <c>TableVersionCell</c>; <paramref name="contextId"/> becomes the context of its variable.
    /// A coordinate this method is never called for is a cell the source does not carry.
    /// </summary>
    public void AddCell(int? column, int? row = null, int? sheet = null, bool isExcluded = false, int? contextId = null)
    {
        var cellId = _nextCell++;
        int? variableVId = null;
        if (contextId is { } cid)
        {
            variableVId = _nextVariable++;
            Source.VariableVersions.Add(new(variableVId.Value, cid));
        }

        Source.Cells.Add(new(cellId, 1, column, row, sheet));
        Source.TableVersionCells.Add(new(TableVId: 1, cellId, isExcluded, IsVoid: false, variableVId));
    }

    /// <summary>Runs the loader over a fresh real-schema destination. The result owns the temporary database.</summary>
    public Outcome Run(IReadOnlyDictionary<int, (int HierarchyId, string HierarchyCode, int DomainId)>? hierarchyBySubCategoryId = null)
    {
        var directory = Path.Join(Path.GetTempPath(), $"EbaDpm.AxisCell_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "synthetic.db");
        SqliteConnection? connection = null;
        try
        {
            SchemaCreator.Create(path, overwrite: false);
            connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            connection.Open();
            foreach (var sql in _destinationSql)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            // The hierarchies the dictionary loader would have written (the restriction row references them).
            foreach (var hierarchy in (hierarchyBySubCategoryId ?? new Dictionary<int, (int HierarchyId, string HierarchyCode, int DomainId)>()).Values.DistinctBy(h => h.HierarchyId))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO \"mHierarchy\" (\"HierarchyID\", \"HierarchyCode\", \"DomainID\") VALUES ($id, $code, $domain)";
                command.Parameters.AddWithValue("$id", hierarchy.HierarchyId);
                command.Parameters.AddWithValue("$code", hierarchy.HierarchyCode);
                command.Parameters.AddWithValue("$domain", hierarchy.DomainId);
                command.ExecuteNonQuery();
            }

            var tableVersions = new List<Dpm20TableVersionRow>
            {
                new(TableVId: 1, Code: "T_01", Name: "Synthetic table", TableId: 1, AbstractTableId: null,
                    StartReleaseId: 1, EndReleaseId: null, IsAbstract: false, ContextId: TableContextId),
            };

            var result = Dpm20AxisAndCellLoader.Load(
                Source, connection,
                new Dictionary<int, int> { [1] = 1 },
                tableVersions,
                hierarchyBySubCategoryId ?? new Dictionary<int, (int HierarchyId, string HierarchyCode, int DomainId)>());

            return new Outcome(directory, connection, result);
        }
        catch
        {
            connection?.Dispose();
            Cleanup(directory);
            throw;
        }
    }

    internal static void Cleanup(string directory)
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

    public sealed record CellRow(long CellId, string? BusinessCode, long IsShaded, string? Dps, string? DatapointSignature);

    /// <summary>The loader result plus the queries the tests need, all by business key.</summary>
    public sealed class Outcome(string directory, SqliteConnection connection, Dpm20AxisAndCellLoader.Result result) : IDisposable
    {
        public SqliteConnection Connection { get; } = connection;

        public Dpm20AxisAndCellLoader.Result Result { get; } = result;

        public long Count(string sql) => QueryHelpers.Scalar(Connection, sql);

        public List<CellRow> Cells()
            => QueryHelpers.Rows(
                    Connection,
                    "SELECT \"CellID\", \"BusinessCode\", \"IsShaded\", \"DPS\", \"DatapointSignature\" FROM \"mTableCell\" ORDER BY \"CellID\"",
                    5)
                .Select(r => new CellRow(long.Parse(r[0]!), r[1], long.Parse(r[2]!), r[3], r[4]))
                .ToList();

        /// <summary>The cell with exactly this BusinessCode (fails if it is not unique).</summary>
        public CellRow Cell(string businessCode) => Assert.Single(Cells(), c => c.BusinessCode == businessCode);

        /// <summary>The ordinate codes at the positions of a cell (mCellPosition), sorted ordinally.</summary>
        public List<string> PositionCodes(long cellId)
            => QueryHelpers.Rows(
                    Connection,
                    $"""
                    SELECT o."OrdinateCode" FROM "mCellPosition" p
                    JOIN "mAxisOrdinate" o ON o."OrdinateID" = p."OrdinateID"
                    WHERE p."CellID" = {cellId}
                    """,
                    1)
                .Select(r => r[0]!)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// The categorisation of the ordinate with this code as sorted <c>DimensionCode=MemberCode</c>
        /// business keys (the open sentinel prints as <c>&lt;open&gt;</c>).
        /// </summary>
        public List<string> Categorisation(string ordinateCode)
            => CategorisationRows(ordinateCode).Select(r => r.Pair).OrderBy(s => s, StringComparer.Ordinal).ToList();

        public List<(string Pair, string? Dps, string? Dms)> CategorisationRows(string ordinateCode)
            => QueryHelpers.Rows(
                    Connection,
                    $"""
                    SELECT d."DimensionCode",
                           CASE WHEN c."MemberID" = {OpenSentinel} THEN '<open>' ELSE m."MemberCode" END,
                           c."DPS", c."DimensionMemberSignature"
                    FROM "mOrdinateCategorisation" c
                    JOIN "mAxisOrdinate" o ON o."OrdinateID" = c."OrdinateID"
                    JOIN "mDimension" d ON d."DimensionID" = c."DimensionID"
                    LEFT JOIN "mMember" m ON m."MemberID" = c."MemberID"
                    WHERE o."OrdinateCode" = '{ordinateCode}'
                    """,
                    4)
                .Select(r => ($"{r[0]}={r[1]}", r[2], r[3]))
                .ToList();

        /// <summary>The ordinate codes of the closed axes, as <c>Code(Level,Order,Parent)</c> in axis then order sequence.</summary>
        public List<string> ClosedOrdinates()
            => QueryHelpers.Rows(
                    Connection,
                    """
                    SELECT o."OrdinateCode", o."Level", o."Order", p."OrdinateCode"
                    FROM "mAxisOrdinate" o
                    JOIN "mAxis" a ON a."AxisID" = o."AxisID" AND a."IsOpenAxis" = 0
                    LEFT JOIN "mAxisOrdinate" p ON p."OrdinateID" = o."ParentOrdinateID"
                    ORDER BY o."AxisID", o."Order"
                    """,
                    4)
                .Select(r => $"{r[0]}({r[1]},{r[2]},{r[3] ?? "-"})")
                .ToList();

        public void Dispose()
        {
            Connection.Dispose();
            Cleanup(directory);
        }
    }
}
