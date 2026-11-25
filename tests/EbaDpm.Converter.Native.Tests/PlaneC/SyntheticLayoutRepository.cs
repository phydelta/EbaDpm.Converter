using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.PlaneC;

/// <summary>
/// A small layout repository built in the test with the real schema
/// (<c>LayoutRepositorySchema</c>). A layout "signature cell" is a cell whose column ordinate carries
/// one value per term (<c>dimension(member)</c>), exactly what <c>PlaneCLayoutReader</c> rebuilds a
/// signature from; key declarations add <c>dimension(*)</c> to every cell of the sheet.
/// </summary>
internal sealed class SyntheticLayoutRepository : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), $"layouts-plc_{Environment.ProcessId}_{Guid.NewGuid():N}");
    private readonly Dictionary<(long Sheet, string Dimension), long> _declarations = [];
    private readonly Dictionary<string, long> _fileByRelease = new(StringComparer.Ordinal);
    private long _next;

    public SyntheticLayoutRepository(bool withHierarchyColumns = true, bool withEqualityRules = true)
    {
        Directory.CreateDirectory(_directory);
        FilePath = Path.Join(_directory, "layouts.db");
        Connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString());
        Connection.Open();
        EbaDpm.Converter.Core.Layouts.LayoutRepositorySchema.Create(Connection);
        if (!withHierarchyColumns)
        {
            Exec("ALTER TABLE LayoutValue DROP COLUMN HierarchyDomainCode");
            Exec("ALTER TABLE LayoutValue DROP COLUMN HierarchyCode");
        }

        if (!withEqualityRules)
        {
            Exec("DROP TABLE LayoutEqualityRule");
        }
    }

    public string FilePath { get; }

    public SqliteConnection Connection { get; }

    public void Dispose()
    {
        Connection.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Left for the OS temp cleanup.
        }
    }

    private void Exec(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private long NextId() => ++_next;

    /// <summary>A sheet of the layouts of release <paramref name="releaseLabel"/> (one file per release).</summary>
    public long Sheet(string releaseLabel, string tableCode)
    {
        if (!_fileByRelease.TryGetValue(releaseLabel, out var fileId))
        {
            fileId = NextId();
            Exec(
                "INSERT INTO LayoutFile (FileId, Path, Sha256, FrameworkCode, ModuleCode, ReleaseLabel) VALUES ($f, 'f.xlsx', '', 'TEST', NULL, $r)",
                ("$f", fileId), ("$r", releaseLabel));
            _fileByRelease[releaseLabel] = fileId;
        }

        var sheetId = NextId();
        Exec(
            "INSERT INTO LayoutSheet (SheetId, FileId, SheetName, TableCode) VALUES ($s, $f, $n, $t)",
            ("$s", sheetId), ("$f", fileId), ("$n", tableCode), ("$t", tableCode));
        return sheetId;
    }

    private long Declaration(long sheetId, string dimension, bool isKey = false)
    {
        if (_declarations.TryGetValue((sheetId, dimension), out var existing))
        {
            return existing;
        }

        var declId = NextId();
        Exec(
            "INSERT INTO LayoutDeclaration (DeclId, SheetId, Region, CellRef, DimensionCode, DomainCode, IsKey) " +
            "VALUES ($d, $s, 'Columns', 'A1', $dim, $dom, $k)",
            ("$d", declId), ("$s", sheetId), ("$dim", dimension), ("$dom", dimension), ("$k", isKey ? 1L : 0L));
        _declarations[(sheetId, dimension)] = declId;
        return declId;
    }

    private long Ordinate(long sheetId, string axis, string code)
    {
        var id = NextId();
        Exec(
            "INSERT INTO LayoutOrdinate (OrdinateId, SheetId, Axis, OrdinateCode) VALUES ($o, $s, $a, $c)",
            ("$o", id), ("$s", sheetId), ("$a", axis), ("$c", code));
        return id;
    }

    /// <summary>A key (open axis) declaration of the sheet: adds <c>dimension(*)</c> to every cell of it.</summary>
    public void KeyDeclaration(long sheetId, string dimension) => Declaration(sheetId, dimension, isKey: true);

    /// <summary>One non-shaded cell whose layout signature is the given terms, each <c>dim(member)</c>
    /// (<c>MET(q1)</c> for the metric). Row and column ordinates are new for the cell.</summary>
    public long SignatureCell(long sheetId, params string[] terms) => SignatureCell(sheetId, shaded: false, terms);

    public long SignatureCell(long sheetId, bool shaded, params string[] terms)
    {
        var n = NextId();
        var rowOrdinate = Ordinate(sheetId, "Y", $"R{n}");
        var columnOrdinate = Ordinate(sheetId, "X", $"C{n}");
        foreach (var term in terms)
        {
            var open = term.IndexOf('(', StringComparison.Ordinal);
            var dimension = term[..open];
            var member = term[(open + 1)..^1];
            Exec(
                "INSERT INTO LayoutValue (ValueId, SheetId, DeclId, OrdinateId, DomainCode, MemberCode, CellRef) " +
                "VALUES ($v, $s, $d, $o, $dom, $m, 'A1')",
                ("$v", NextId()), ("$s", sheetId), ("$d", Declaration(sheetId, dimension)), ("$o", columnOrdinate),
                ("$dom", dimension), ("$m", member));
        }

        var cellId = NextId();
        Exec(
            "INSERT INTO LayoutCell (CellId, SheetId, RowOrdinateId, ColumnOrdinateId, CellRef, IsShaded) VALUES ($c, $s, $r, $x, $ref, $sh)",
            ("$c", cellId), ("$s", sheetId), ("$r", rowOrdinate), ("$x", columnOrdinate), ("$ref", $"Z{cellId}"), ("$sh", shaded ? 1L : 0L));
        return cellId;
    }

    /// <summary>A metric annotation with a hierarchy bracket, <c>(metric) label[domain:hierarchy]</c>, on
    /// the ordinate <paramref name="ordinateCode"/> of the axis. With <paramref name="hierarchyDomain"/>
    /// and <paramref name="hierarchy"/> null it is a metric without a bracket (not a unit of C-MHR-01).</summary>
    public void MetricOrdinate(
        long sheetId, string axis, string ordinateCode, string metric, string? hierarchyDomain, string? hierarchy, bool sheetLevel = false)
    {
        var ordinate = sheetLevel ? (long?)null : Ordinate(sheetId, axis, ordinateCode);
        Exec(
            "INSERT INTO LayoutValue (ValueId, SheetId, DeclId, OrdinateId, DomainCode, MemberCode, CellRef, HierarchyDomainCode, HierarchyCode) " +
            "VALUES ($v, $s, $d, $o, 'MET', $m, 'A1', $hd, $h)",
            ("$v", NextId()), ("$s", sheetId), ("$d", Declaration(sheetId, "MET")), ("$o", ordinate), ("$m", metric),
            ("$hd", hierarchyDomain), ("$h", hierarchy));
    }

    /// <summary>One term of an equality rule (<c>==</c>): the cell at (row, column[, sheet]) of a table.</summary>
    public sealed record RuleTerm(string TableCode, string Row, string Column, string? Z = null)
    {
        public string Text => $"{{{TableCode}, r{Row}, c{Column}{(Z is null ? string.Empty : ", s" + Z)}}}";
    }

    public void EqualityRule(long ruleId, long sheetId, params RuleTerm[] terms)
    {
        for (var i = 0; i < terms.Length; i++)
        {
            Exec(
                "INSERT INTO LayoutEqualityRule (RuleId, Position, SourceFile, SheetId, CellRef, Term, TableCode, RowCode, ColumnCode, ZCode) " +
                "VALUES ($r, $p, 'f.xlsx', $s, 'A1', $t, $tc, $row, $col, $z)",
                ("$r", ruleId), ("$p", (long)i), ("$s", sheetId), ("$t", terms[i].Text), ("$tc", terms[i].TableCode),
                ("$row", terms[i].Row), ("$col", terms[i].Column), ("$z", terms[i].Z));
        }
    }
}
