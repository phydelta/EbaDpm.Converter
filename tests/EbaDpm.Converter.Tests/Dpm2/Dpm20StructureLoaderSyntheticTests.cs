using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Mapping.Dpm20;
using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Synthetic tests of <see cref="Dpm20StructureLoader.Load"/> through
/// <see cref="InMemoryStructureSource"/>: no Access, no data files. Rule under test:
/// docs/mapping-dpm2.md, section "mTemplateOrTable" ("Parent of a template": the emitted group
/// that contains the template or any of its concrete children, because
/// <c>TableGroupComposition</c> never points to abstract tables).
/// </summary>
public sealed class Dpm20StructureLoaderSyntheticTests : IDisposable
{
    private readonly string _tempDirectory;

    public Dpm20StructureLoaderSyntheticTests()
    {
        _tempDirectory = Path.Join(Path.GetTempPath(), $"EbaDpm.StructSynth_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort.
        }
    }

    [Fact]
    public void AbstractTemplate_TakesItsParentGroupFromItsConcreteChildren()
    {
        // Abstract template T_01.00 (TableID 50) has one concrete child T_01.00.a (TableID 51).
        // Group G_ONE composes only the child (never the abstract table); group G_OTHER composes
        // an unrelated table. The template node must hang under G_ONE, the table under the template.
        var source = new InMemoryStructureSource { CutoffReleaseCode = "R42" };
        source.TableGroups.AddRange(
        [
            new(7, "G_ONE", "Group one", "templateGroup", 1, null),
            new(8, "G_OTHER", "Group other", "templateGroup", 1, null),
        ]);
        source.TableGroupCompositions.AddRange([new(7, 51), new(8, 52)]);

        Dpm20TableVersionRow[] tableVersions =
        [
            new(500, "T_01.00", "Template one", 50, null, 1, null, true, null),
            new(501, "T_01.00.a", "Table one a", 51, 50, 1, null, false, null),
            new(502, "U_01.00", "Table unrelated", 52, null, 1, null, false, null),
        ];
        AccessTaxonomyRow[] taxonomies = [new(1, null, "TAX_R42", null, null, null, null, "R42", null)];
        var derivation = new Dpm20TaxonomyDerivationResult(
            taxonomies,
            [],
            new Dictionary<int, string> { [1] = "TAX_R42" },
            new Dictionary<int, IReadOnlyList<int>> { [1] = new[] { 501, 502 } });

        var path = Path.Join(_tempDirectory, "out.db");
        SchemaCreator.Create(path, overwrite: false);

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        Execute(connection, "INSERT INTO mTaxonomy (TaxonomyID, TaxonomyCode) VALUES (1, 'TAX_R42')");
        // The loader uses 0 as the root parent (never NULL); the real pipeline writes with
        // foreign keys off, so here the root row is seeded to keep them enforced.
        Execute(connection, "INSERT INTO mTemplateOrTable (TemplateOrTableID, TemplateOrTableCode) VALUES (0, 'ROOT')");

        var result = Dpm20StructureLoader.Load(source, connection, derivation, tableVersions, taxonomies);

        Assert.Equal(2, result.BusinessTableRows);
        Assert.True(result.PairInvariantHolds);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT t.TemplateOrTableCode, p.TemplateOrTableCode, g.TemplateOrTableCode "
            + "FROM mTemplateOrTable t "
            + "LEFT JOIN mTemplateOrTable p ON p.TemplateOrTableID = t.ParentTemplateOrTableID "
            + "LEFT JOIN mTemplateOrTable g ON g.TemplateOrTableID = p.ParentTemplateOrTableID "
            + "WHERE t.TemplateOrTableCode = 'T_01.00.a' ORDER BY 1";
        using var reader = command.ExecuteReader();
        var chains = new List<(string Table, string? Template, string? Group)>();
        while (reader.Read())
        {
            chains.Add((reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        Assert.Equal([("T_01.00.a", "eba_tgT_01.00", "eba_tgG_ONE")], chains);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
