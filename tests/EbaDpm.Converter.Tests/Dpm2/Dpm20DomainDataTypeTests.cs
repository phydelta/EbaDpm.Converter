using System.Data.OleDb;
using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Mapping.Dpm20;
using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// <c>mDomain.DataType</c> of the DPM 2.0 typed domains (<c>Dpm20DictionaryLoader</c>).
/// Spec: enumerated domains, <c>MET</c> and the sentinel have <c>NULL</c>; a typed domain from
/// <c>Category</c> takes the single distinct raw <c>DataType.Code</c> of the properties linked to it
/// by <c>PropertyCategory</c> (<c>s</c> = NotEmptyString, <c>es</c> = String, others by the metric
/// table, no metric naming law); no typed property, several codes or an untranslatable code leave
/// <c>NULL</c> and a named case in <c>Diagnostics.UnresolvedDomainDataTypeCases</c>.
///
/// The loader takes a concrete <see cref="Dpm20AccessReader"/> (sealed, over OLEDB) and has no
/// seam, so the synthetic tests build a minimal REAL <c>.accdb</c> through ADOX/ACE
/// (<see cref="AceFactAttribute"/>): they need no EBA data file, only the Access Database Engine.
/// </summary>
[Trait("Tier", "Synthetic")]
public sealed class Dpm20DomainDataTypeSyntheticTests
{
    // Category ids: 1 = _PR (required by the loader), 10.. = test categories.
    private const int PrCategory = 1;

    private static (Dpm20DictionaryLoader.Diagnostics Diagnostics, Dictionary<string, string?> DataTypeByDomain, Dictionary<string, bool> TypedByDomain)
        Run(Action<OleDbConnection> populate)
    {
        var accdb = Dpm2TestAccdbFactory.CreateEmptyAccdb();
        var db = Path.Join(Path.GetTempPath(), $"domdt_{Environment.ProcessId}_{Guid.NewGuid():N}.db");
        try
        {
            using (var oledb = new OleDbConnection($"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={accdb};"))
            {
                oledb.Open();
                foreach (var ddl in Ddl)
                {
                    Exec(oledb, ddl);
                }

                Exec(oledb, "INSERT INTO [Release] ([ReleaseID],[Code],[IsCurrent]) VALUES (1,'1.0',-1)");
                Exec(oledb, $"INSERT INTO [Category] ([CategoryID],[Code],[Name],[IsEnumerated]) VALUES ({PrCategory},'_PR','Property',-1)");
                Exec(oledb, "INSERT INTO [Category] ([CategoryID],[Code],[Name],[IsEnumerated]) VALUES (2,'_TE','Template',-1)");
                Exec(oledb, "INSERT INTO [Category] ([CategoryID],[Code],[Name],[IsEnumerated]) VALUES (3,'_NA','Not applicable',-1)");
                foreach (var (id, code) in new[] { (1, "i"), (2, "r"), (3, "s"), (4, "es"), (5, "dt"), (6, "e") })
                {
                    Exec(oledb, $"INSERT INTO [DataType] ([DataTypeID],[Code]) VALUES ({id},'{code}')");
                }

                populate(oledb);
            }

            OleDbConnection.ReleaseObjectPool();

            SchemaCreator.Create(db, overwrite: false);
            using var destination = new SqliteConnection($"Data Source={db};Pooling=False");
            destination.Open();
            using var reader = new Dpm20AccessReader($"{accdb}", cutoffReleaseCode: "1.0");
            reader.Open();
            Dpm20DictionaryLoader.Load(reader, destination, out var diagnostics);

            var dataType = new Dictionary<string, string?>();
            var typed = new Dictionary<string, bool>();
            using var command = destination.CreateCommand();
            command.CommandText = "SELECT DomainCode, DataType, IsTypedDomain FROM mDomain";
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                dataType[rows.GetString(0)] = rows.IsDBNull(1) ? null : rows.GetString(1);
                typed[rows.GetString(0)] = rows.GetInt64(2) != 0;
            }

            return (diagnostics, dataType, typed);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            OleDbConnection.ReleaseObjectPool();
            foreach (var f in new[] { accdb, db })
            {
                try
                {
                    File.Delete(f);
                }
                catch (IOException)
                {
                    // Best-effort cleanup of a temp file.
                }
            }
        }
    }

    private static readonly string[] Ddl =
    [
        "CREATE TABLE [Release] ([ReleaseID] INTEGER, [Code] TEXT(50), [Date] TEXT(50), [Status] TEXT(50), [Description] TEXT(50), [IsCurrent] YESNO)",
        "CREATE TABLE [Category] ([CategoryID] INTEGER, [Code] TEXT(50), [Name] TEXT(100), [Description] TEXT(100), [IsEnumerated] YESNO)",
        "CREATE TABLE [SuperCategoryComposition] ([SuperCategoryID] INTEGER, [CategoryID] INTEGER)",
        "CREATE TABLE [ItemCategory] ([ItemID] INTEGER, [CategoryID] INTEGER, [Code] TEXT(50), [IsDefaultItem] YESNO, [StartReleaseID] INTEGER, [EndReleaseID] INTEGER)",
        "CREATE TABLE [Item] ([ItemID] INTEGER, [Name] TEXT(100))",
        "CREATE TABLE [Variable] ([VariableID] INTEGER, [Type] TEXT(20))",
        "CREATE TABLE [VariableVersion] ([VariableVID] INTEGER, [VariableID] INTEGER, [PropertyID] INTEGER)",
        "CREATE TABLE [Header] ([HeaderID] INTEGER, [IsKey] YESNO)",
        "CREATE TABLE [HeaderVersion] ([HeaderVID] INTEGER, [HeaderID] INTEGER, [PropertyID] INTEGER, [SubCategoryVID] INTEGER)",
        "CREATE TABLE [SubCategory] ([SubCategoryID] INTEGER, [CategoryID] INTEGER, [Code] TEXT(50), [Name] TEXT(100))",
        "CREATE TABLE [SubCategoryVersion] ([SubCategoryVID] INTEGER, [SubCategoryID] INTEGER, [StartReleaseID] INTEGER, [EndReleaseID] INTEGER)",
        "CREATE TABLE [ContextComposition] ([ContextID] INTEGER, [PropertyID] INTEGER)",
        "CREATE TABLE [DataType] ([DataTypeID] INTEGER, [Code] TEXT(20))",
        "CREATE TABLE [Property] ([PropertyID] INTEGER, [DataTypeID] INTEGER)",
        "CREATE TABLE [PropertyCategory] ([PropertyID] INTEGER, [CategoryID] INTEGER, [StartReleaseID] INTEGER, [EndReleaseID] INTEGER)",
    ];

    private static void Exec(OleDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>A category (typed or enumerated) with one property per given raw data type code, linked by PropertyCategory.</summary>
    private static void AddCategory(OleDbConnection c, int categoryId, string code, bool enumerated, params (int PropertyId, int DataTypeId)[] properties)
    {
        Exec(c, $"INSERT INTO [Category] ([CategoryID],[Code],[Name],[IsEnumerated]) VALUES ({categoryId},'{code}','{code} name',{(enumerated ? -1 : 0)})");
        foreach (var (propertyId, dataTypeId) in properties)
        {
            Exec(c, $"INSERT INTO [Property] ([PropertyID],[DataTypeID]) VALUES ({propertyId},{dataTypeId})");
            Exec(c, $"INSERT INTO [PropertyCategory] ([PropertyID],[CategoryID],[StartReleaseID],[EndReleaseID]) VALUES ({propertyId},{categoryId},1,NULL)");
        }
    }

    [AceFact]
    public void TypedCategory_WithOneType_IsTranslated_AndNothingIsReported()
    {
        var (diagnostics, dataType, typed) = Run(c =>
        {
            AddCategory(c, 10, "TI", enumerated: false, (100, 1)); // i
            AddCategory(c, 11, "TR", enumerated: false, (101, 2)); // r
            AddCategory(c, 12, "TMANY", enumerated: false, (102, 1), (103, 1)); // same code twice = one distinct code
        });

        Assert.True(typed["TI"]);
        Assert.Equal("Integer", dataType["TI"]);
        Assert.Equal("Decimal", dataType["TR"]);
        Assert.Equal("Integer", dataType["TMANY"]);
        Assert.Empty(diagnostics.UnresolvedDomainDataTypeCases);
    }

    [AceFact]
    public void StringCodes_s_And_es_AreNotEmptyString_And_String_WithoutTheMetricNamingLaw()
    {
        var (diagnostics, dataType, _) = Run(c =>
        {
            AddCategory(c, 10, "TS", enumerated: false, (100, 3)); // s
            AddCategory(c, 11, "TES", enumerated: false, (101, 4)); // es
        });

        Assert.Equal("NotEmptyString", dataType["TS"]);
        Assert.Equal("String", dataType["TES"]);
        Assert.Empty(diagnostics.UnresolvedDomainDataTypeCases);
    }

    [AceFact]
    public void TypedCategory_WithTwoDistinctTypes_IsNull_AndNamedInTheDiagnostic()
    {
        var (diagnostics, dataType, typed) = Run(c =>
            AddCategory(c, 10, "TTWO", enumerated: false, (100, 1), (101, 3))); // i + s

        Assert.True(typed["TTWO"]); // row kept
        Assert.Null(dataType["TTWO"]);
        var line = Assert.Single(diagnostics.UnresolvedDomainDataTypeCases);
        Assert.Contains("TTWO", line, StringComparison.Ordinal);
        Assert.Contains("'i'", line, StringComparison.Ordinal);
        Assert.Contains("'s'", line, StringComparison.Ordinal);
    }

    [AceFact]
    public void TypedCategory_WithUntranslatableCode_IsNull_AndNamedInTheDiagnostic()
    {
        var (diagnostics, dataType, typed) = Run(c =>
            AddCategory(c, 10, "TDT", enumerated: false, (100, 5))); // dt

        Assert.True(typed["TDT"]);
        Assert.Null(dataType["TDT"]);
        var line = Assert.Single(diagnostics.UnresolvedDomainDataTypeCases);
        Assert.Contains("TDT", line, StringComparison.Ordinal);
        Assert.Contains("'dt'", line, StringComparison.Ordinal);
    }

    [AceFact]
    public void TypedCategory_WithoutProperties_IsNull_AndNamedInTheDiagnostic()
    {
        var (diagnostics, dataType, typed) = Run(c =>
            AddCategory(c, 10, "TEMPTY", enumerated: false));

        Assert.True(typed["TEMPTY"]);
        Assert.Null(dataType["TEMPTY"]);
        var line = Assert.Single(diagnostics.UnresolvedDomainDataTypeCases);
        Assert.Contains("TEMPTY", line, StringComparison.Ordinal);
    }

    [AceFact]
    public void EnumeratedCategory_WithEnumerationProperties_IsNull_AndNotReported()
    {
        var (diagnostics, dataType, typed) = Run(c =>
        {
            AddCategory(c, 10, "EN", enumerated: true, (100, 6)); // e
            AddCategory(c, 11, "EN2", enumerated: true); // no property at all
            // A typed domain next to them proves the diagnostic list is not simply empty by construction.
            AddCategory(c, 12, "TBAD", enumerated: false, (101, 5));
        });

        Assert.False(typed["EN"]);
        Assert.Null(dataType["EN"]);
        Assert.False(typed["EN2"]);
        Assert.Null(dataType["EN2"]);
        Assert.Null(dataType["MET"]);
        Assert.Null(dataType[string.Empty]); // sentinel
        Assert.Equal("Integer", dataType["INT_NA"]);
        Assert.Equal("NotEmptyString", dataType["STR_NA"]);

        var line = Assert.Single(diagnostics.UnresolvedDomainDataTypeCases); // only TBAD, never EN/EN2
        Assert.Contains("TBAD", line, StringComparison.Ordinal);
    }
}

/// <summary>
/// The same rule over the real 4.2.1 and 4.3 Access databases. The expected value is derived here
/// from the Access tables with an independent query (not from the loader) and compared by
/// <c>DomainCode</c>.
/// </summary>
[Trait("Tier", "RealData")]
public sealed class Dpm20DomainDataTypeRealDataTests : IDisposable
{
    private static readonly HashSet<string> ExcludedCategories = new(StringComparer.Ordinal) { "SE", "_NA", "_PR", "_TE" };

    private static readonly Dictionary<string, string> Translation = new(StringComparer.Ordinal)
    {
        ["s"] = "NotEmptyString", ["es"] = "String", ["i"] = "Integer", ["r"] = "Decimal", ["b"] = "Boolean",
        ["t"] = "BooleanTrue", ["d"] = "Date", ["e"] = "Enumeration/Code", ["m"] = "Monetary", ["p"] = "Percent",
    };

    private sealed record Release(string Path, string TempDir, string Db, IReadOnlyList<string> Unresolved,
        Dictionary<string, (string? DataType, bool Typed)> Output, Dictionary<string, (bool Typed, SortedSet<string> Codes)> Source);

    private readonly string? _tempRoot;
    private readonly Release? _r42;
    private readonly Release? _r43;

    public Dpm20DomainDataTypeRealDataTests()
    {
        if (!RepoPaths.IsDataAvailable)
        {
            return;
        }

        _tempRoot = Path.Join(Path.GetTempPath(), $"EbaDpm.DomainDataType_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        RepoPaths.EnsureAccessDpm20DatabaseExists();
        RepoPaths.EnsureAccessDpm20Release43DatabaseExists();
        _r42 = Build(RepoPaths.AccessDpm20DatabasePath, "4.2", "r42");
        _r43 = Build(RepoPaths.AccessDpm20Release43DatabasePath, null, "r43");
    }

    private Release Build(string accdb, string? cutoff, string name)
    {
        var db = Path.Join(_tempRoot!, name + ".db");
        SchemaCreator.Create(db, overwrite: false);
        IReadOnlyList<string> unresolved;
        Dictionary<string, (string?, bool)> output = [];
        using (var destination = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            destination.Open();
            using var reader = new Dpm20AccessReader(accdb, cutoffReleaseCode: cutoff);
            reader.Open();
            Dpm20DictionaryLoader.Load(reader, destination, out var diagnostics);
            unresolved = diagnostics.UnresolvedDomainDataTypeCases;
            using var command = destination.CreateCommand();
            command.CommandText = "SELECT DomainCode, DataType, IsTypedDomain FROM mDomain";
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                output[rows.GetString(0)] = (rows.IsDBNull(1) ? null : rows.GetString(1), rows.GetInt64(2) != 0);
            }
        }

        var source = new Dictionary<string, (bool Typed, SortedSet<string> Codes)>(StringComparer.Ordinal);
        using (var oledb = new OleDbConnection($"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={accdb};"))
        {
            oledb.Open();
            using (var command = oledb.CreateCommand())
            {
                command.CommandText = "SELECT [Code], [IsEnumerated] FROM [Category]";
                using var rows = command.ExecuteReader();
                while (rows.Read())
                {
                    source[rows.GetString(0)] = (!Convert.ToBoolean(rows.GetValue(1)), new SortedSet<string>(StringComparer.Ordinal));
                }
            }

            using (var command = oledb.CreateCommand())
            {
                command.CommandText =
                    "SELECT DISTINCT c.[Code], dt.[Code] FROM (([PropertyCategory] pc INNER JOIN [Category] c ON c.[CategoryID] = pc.[CategoryID]) "
                    + "INNER JOIN [Property] p ON p.[PropertyID] = pc.[PropertyID]) INNER JOIN [DataType] dt ON dt.[DataTypeID] = p.[DataTypeID]";
                using var rows = command.ExecuteReader();
                while (rows.Read())
                {
                    source[rows.GetString(0)].Codes.Add(rows.GetString(1));
                }
            }
        }

        OleDbConnection.ReleaseObjectPool();
        SqliteConnection.ClearAllPools();
        return new Release(accdb, _tempRoot!, db, unresolved, output, source);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (_tempRoot is not null && Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private static IEnumerable<(string Code, SortedSet<string> Codes)> TypedFromCategory(Release r)
        => r.Source.Where(kv => kv.Value.Typed && !ExcludedCategories.Contains(kv.Key)).Select(kv => (kv.Key, kv.Value.Codes));

    private void AssertRelease(Release r)
    {
        var typed = TypedFromCategory(r).ToList();

        // Positive control: the typed set is non-empty and at least one typed domain has an expected
        // non-NULL type, so the old behaviour (all NULL) would fail this test.
        Assert.NotEmpty(typed);
        Assert.Contains(typed, t => t.Codes.Count == 1 && Translation.ContainsKey(t.Codes.Single()));

        var problems = new List<string>();
        foreach (var (code, codes) in typed)
        {
            string? expected = codes.Count == 1 ? Translation.GetValueOrDefault(codes.Single()) : null;
            if (!r.Output.TryGetValue(code, out var actual))
            {
                problems.Add($"{code}: missing in mDomain");
            }
            else if (!actual.Typed)
            {
                problems.Add($"{code}: IsTypedDomain=0 in the output");
            }
            else if (actual.DataType != expected)
            {
                problems.Add($"{code}: expected '{expected ?? "NULL"}' (source codes: {string.Join(",", codes)}) actual '{actual.DataType ?? "NULL"}'");
            }
            else if (actual.DataType is null)
            {
                problems.Add($"{code}: source codes {{{string.Join(",", codes)}}} give NULL");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(r.Unresolved.Count == 0, "UnresolvedDomainDataTypeCases: " + string.Join(" | ", r.Unresolved));

        // Every enumerated domain, MET and the sentinel: NULL.
        var notNull = r.Output
            .Where(kv => !kv.Value.Typed && kv.Value.DataType is not null)
            .Select(kv => $"{kv.Key}={kv.Value.DataType}")
            .ToList();
        Assert.True(notNull.Count == 0, "Non-typed domains with a DataType: " + string.Join(", ", notNull));
        Assert.Contains(r.Output, kv => !kv.Value.Typed); // the check above examined something
        Assert.Null(r.Output["MET"].DataType);
        Assert.Null(r.Output[string.Empty].DataType);
        Assert.Equal("Integer", r.Output["INT_NA"].DataType);
        Assert.Equal("NotEmptyString", r.Output["STR_NA"].DataType);
    }

    [DataFact]
    public void Release42_TypedDomainsHaveTheirDataType_EnumeratedHaveNull_NothingUnresolved() => AssertRelease(_r42!);

    [DataFact]
    public void Release43_TypedDomainsHaveTheirDataType_EnumeratedHaveNull_NothingUnresolved() => AssertRelease(_r43!);

    /// <summary>
    /// Regression against the 4.2 reference export, by <c>DomainCode</c>, for the typed domains
    /// present in both. <c>mDomain.DataType</c> is not compared by the plane-B validation checks and
    /// no known exception names it (verified in <c>KnownExceptions.cs</c> and the validation checks).
    /// </summary>
    [DataFact]
    public void Release42_TypedDomainDataType_EqualsTheReferenceExport_ByDomainCode()
    {
        RepoPaths.EnsureReferenceDatabaseExists();
        var reference = new Dictionary<string, string?>(StringComparer.Ordinal);
        using (var connection = new SqliteConnection($"Data Source={RepoPaths.ReferenceDatabasePath};Mode=ReadOnly;Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DomainCode, DataType FROM mDomain WHERE IsTypedDomain = 1";
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                reference[rows.GetString(0)] = rows.IsDBNull(1) ? null : rows.GetString(1);
            }
        }

        var generated = _r42!.Output.Where(kv => kv.Value.Typed).ToDictionary(kv => kv.Key, kv => kv.Value.DataType, StringComparer.Ordinal);
        var common = reference.Keys.Intersect(generated.Keys, StringComparer.Ordinal).ToList();

        // The control: the comparison covers the typed domains of the reference, not an empty set.
        Assert.True(common.Count >= 15, $"Only {common.Count} typed domains in common.");
        Assert.Contains(common, c => reference[c] is not null);

        var diffs = common
            .Where(c => reference[c] != generated[c])
            .Select(c => $"{c}: reference '{reference[c] ?? "NULL"}' generated '{generated[c] ?? "NULL"}'")
            .ToList();
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
    }
}
