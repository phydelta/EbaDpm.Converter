using System.Globalization;
using EbaDpm.Converter.Native.Catalog;
using EbaDpm.Converter.Native.Schema;
using EbaDpm.Converter.Native.Tests.Support;
using Microsoft.Data.Sqlite;
using static EbaDpm.Converter.Native.Tests.Support.Cat;

namespace EbaDpm.Converter.Native.Tests.Schema;

/// <summary>
/// The DDL built from a catalogue (docs/target-schema-native.md, "Keys, relationships and
/// indexes"). The DDL is run in a real in-memory SQLite database and inspected with PRAGMAs, so
/// the assertions are about what SQLite understood, not only about the text.
/// </summary>
public sealed class NativeSchemaBuilderTests
{
    private static SqliteConnection Run(NativeSchemaDdl ddl)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var statement in ddl.Statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        return connection;
    }

    private static List<object?[]> Rows(SqliteConnection c, string sql) => AccessSqliteComparer.Rows(c, sql);

    // ------------------------------------------------------------------ exclusions

    [Fact]
    public void ExcludedTables_AreExactlyTheElevenNamedInTheSpecification()
    {
        string[] specification =
        [
            "ChangeLog", "ChangeLogAttribute", "User", "Role", "UserRole",
            "Aux_CellMapping", "Aux_CellStatus", "ModelViolations", "VarGeneration_Detail", "VarGeneration_Summary", "VariableGeneration",
        ];

        Assert.Equal(11, NativeExclusions.ExcludedTables.Count);
        Assert.Equal(specification.Order(StringComparer.Ordinal), NativeExclusions.ExcludedTables.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Build_DoesNotExportTheElevenNamedTables_AndExportsEveryOtherTable()
    {
        // The closed list is a list of names, not a pattern: look-alikes are exported.
        string[] lookAlikes = ["ChangeLogX", "Aux_Other", "UserX", "Roles", "Aux_CellMappingX", "VarGeneration", "XChangeLog", "ChangeLog2"];
        var tables = NativeExclusions.ExcludedTables.Concat(lookAlikes).Concat(["Plain"]).Select(n => Simple(n)).ToList();

        var ddl = NativeSchemaBuilder.Build(Make(tables));

        var exported = ddl.Tables.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(lookAlikes.Concat(["Plain"]).Order(StringComparer.Ordinal), exported.Order(StringComparer.Ordinal));
        foreach (var excluded in NativeExclusions.ExcludedTables)
        {
            Assert.DoesNotContain(excluded, exported);
        }

        using var sqlite = Run(ddl);
        var created = AccessSqliteComparer.SqliteUserTables(sqlite);
        Assert.Equal(lookAlikes.Concat(["Plain"]).Order(StringComparer.Ordinal), created.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Build_PositiveControl_EachExcludedNameIsIndividuallyDetectedAsExcluded()
    {
        // A catalogue with only one of the 11 names yields no table: the exclusion check can
        // fail for each name, one by one, and is not satisfied by the others.
        foreach (var name in NativeExclusions.ExcludedTables)
        {
            Assert.Empty(NativeSchemaBuilder.Build(Make([Simple(name)])).Tables);
        }

        Assert.Single(NativeSchemaBuilder.Build(Make([Simple("ChangeLogX")])).Tables);
    }

    // ------------------------------------------------------------------ DDL content

    [Fact]
    public void Build_QuotesKeywordNames_AndSqliteAcceptsThem()
    {
        var catalog = Make(
            [
                Table("Table", ["Order"], Col("Order", nullable: false), Col("Select")),
                Table("Release", ["Id"], Col("Id", nullable: false), Col("Table")),
                Table("Reference", ["Id"], Col("Id", nullable: false), Col("Group")),
            ],
            [Fk("fk_ref", "Reference", "Group", "Table", "Order")]);

        var ddl = NativeSchemaBuilder.Build(catalog);

        Assert.Contains(ddl.TableStatements, s => s.StartsWith("CREATE TABLE \"Table\" (", StringComparison.Ordinal));
        Assert.Contains(ddl.TableStatements, s => s.StartsWith("CREATE TABLE \"Release\" (", StringComparison.Ordinal));
        Assert.Contains(ddl.TableStatements, s => s.StartsWith("CREATE TABLE \"Reference\" (", StringComparison.Ordinal));
        Assert.Contains(ddl.TableStatements, s => s.Contains("\"Select\" INTEGER", StringComparison.Ordinal));
        Assert.Contains(ddl.TableStatements, s => s.Contains("REFERENCES \"Table\" (\"Order\")", StringComparison.Ordinal));

        using var sqlite = Run(ddl);
        Assert.Equal(["Reference", "Release", "Table"], AccessSqliteComparer.SqliteUserTables(sqlite));
    }

    [Fact]
    public void Build_NotNull_OnlyWhereTheAccessColumnIsRequired()
    {
        var catalog = Make([Table("T", ["Id"], Col("Id", nullable: false), Col("Required", Text, nullable: false), Col("Optional", Text))]);

        using var sqlite = Run(NativeSchemaBuilder.Build(catalog));

        var columns = Rows(sqlite, "PRAGMA table_info(\"T\")")
            .ToDictionary(r => (string)r[1]!, r => (Type: (string)r[2]!, NotNull: Convert.ToInt32(r[3], CultureInfo.InvariantCulture)));
        Assert.Equal(("INTEGER", 1), columns["Id"]);
        Assert.Equal(("TEXT", 1), columns["Required"]);
        Assert.Equal(("TEXT", 0), columns["Optional"]);
    }

    [Fact]
    public void Build_ColumnTypes_FollowTheMapping_AndColumnOrderIsTheCatalogueOrder()
    {
        var catalog = Make(
            [
                Table(
                    "T", ["Id"],
                    Col("Zeta", 130), Col("Id", 3, nullable: false), Col("Flag", 11), Col("Guid", 72), Col("Dec", 131),
                    Col("When", 7), Col("Small", 2), Col("Byte", 17), Col("Big", 20)),
            ]);

        using var sqlite = Run(NativeSchemaBuilder.Build(catalog));

        var columns = Rows(sqlite, "PRAGMA table_info(\"T\")").Select(r => ((string)r[1]!, (string)r[2]!)).ToList();
        Assert.Equal(
            [
                ("Zeta", "TEXT"), ("Id", "INTEGER"), ("Flag", "INTEGER"), ("Guid", "TEXT"), ("Dec", "TEXT"),
                ("When", "TEXT"), ("Small", "INTEGER"), ("Byte", "INTEGER"), ("Big", "INTEGER"),
            ],
            columns);
    }

    [Fact]
    public void Build_CompositePrimaryKey_KeepsTheKeyOrderOfTheCatalogue()
    {
        var catalog = Make(
            [
                Table("ContextComposition", ["ContextID", "PropertyID"], Col("PropertyID", nullable: false), Col("ContextID", nullable: false), Col("ItemID")),
                Table("Single", ["Id"], Col("Id", nullable: false)),
            ]);

        using var sqlite = Run(NativeSchemaBuilder.Build(catalog));

        var key = Rows(sqlite, "PRAGMA table_info(\"ContextComposition\")")
            .Where(r => Convert.ToInt32(r[5], CultureInfo.InvariantCulture) > 0)
            .OrderBy(r => Convert.ToInt32(r[5], CultureInfo.InvariantCulture))
            .Select(r => (string)r[1]!).ToList();
        Assert.Equal(["ContextID", "PropertyID"], key);
        Assert.Single(Rows(sqlite, "PRAGMA table_info(\"Single\")"), r => Convert.ToInt32(r[5], CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public void Build_ForeignKeys_CarryTheAccessRules()
    {
        var catalog = Make(
            [Simple("Parent"), Simple("Child", "ParentA", "ParentB", "ParentC")],
            [
                Fk("fk_a", "Child", "ParentA", "Parent", update: "CASCADE", delete: "CASCADE"),
                Fk("fk_b", "Child", "ParentB", "Parent", update: "CASCADE", delete: "NO ACTION"),
                Fk("fk_c", "Child", "ParentC", "Parent", update: "NO ACTION", delete: "SET NULL"),
            ]);

        using var sqlite = Run(NativeSchemaBuilder.Build(catalog));

        var rules = Rows(sqlite, "PRAGMA foreign_key_list(\"Child\")")
            .ToDictionary(r => (string)r[3]!, r => (Table: (string)r[2]!, To: (string)r[4]!, OnUpdate: (string)r[5]!, OnDelete: (string)r[6]!));
        Assert.Equal(3, rules.Count);
        Assert.Equal(("Parent", "Id", "CASCADE", "CASCADE"), rules["ParentA"]);
        Assert.Equal(("Parent", "Id", "CASCADE", "NO ACTION"), rules["ParentB"]);
        Assert.Equal(("Parent", "Id", "NO ACTION", "SET NULL"), rules["ParentC"]);
    }

    [Fact]
    public void Build_ForeignKeyToOrFromAnExcludedTable_IsDropped_OthersAreKept()
    {
        var catalog = Make(
            [Simple("Kept"), Simple("Child", "KeptId", "LogId"), Simple("ChangeLog", "KeptId"), Simple("VariableGeneration", "ChildId")],
            [
                Fk("fk_ok", "Child", "KeptId", "Kept"),
                Fk("fk_to_excluded", "Child", "LogId", "ChangeLog"),
                Fk("fk_from_excluded", "ChangeLog", "KeptId", "Kept"),
                Fk("fk_working", "VariableGeneration", "ChildId", "Child"),
            ]);

        var ddl = NativeSchemaBuilder.Build(catalog);

        Assert.Equal(["fk_ok"], ddl.ForeignKeysDeclared.Select(f => f.Name));
        Assert.Equal(
            ["fk_from_excluded", "fk_to_excluded", "fk_working"],
            ddl.ForeignKeysDropped.Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(ddl.Statements, s => s.Contains("\"ChangeLog\"", StringComparison.Ordinal));
        Assert.DoesNotContain(ddl.Statements, s => s.Contains("\"VariableGeneration\"", StringComparison.Ordinal));

        using var sqlite = Run(ddl);
        Assert.Single(Rows(sqlite, "PRAGMA foreign_key_list(\"Child\")"));
    }

    // ------------------------------------------------------------------ indexes

    [Fact]
    public void Build_AccessIndexes_AreReproducedWithColumnsOrderAndUniqueness()
    {
        var catalog = Make(
            [Simple("T", "A", "B", "C")],
            indexes:
            [
                Index("T", "ix_AB", unique: false, "A", "B"),
                Index("T", "uq_C", unique: true, "C"),
                Index("T", "ix_BA", unique: false, "B", "A"),
            ]);

        var ddl = NativeSchemaBuilder.Build(catalog);
        using var sqlite = Run(ddl);

        Assert.Equal(["T_ix_AB", "T_ix_BA", "T_uq_C"], ddl.AccessIndexNames.Order(StringComparer.Ordinal));
        var indexes = Rows(sqlite, "PRAGMA index_list(\"T\")")
            .Where(r => (string)r[3]! == "c") // origin 'c' = CREATE INDEX (not the PK's autoindex)
            .ToDictionary(r => (string)r[1]!, r => Convert.ToInt32(r[2], CultureInfo.InvariantCulture));
        Assert.Equal(1, indexes["T_uq_C"]);
        Assert.Equal(0, indexes["T_ix_AB"]);
        Assert.Equal(0, indexes["T_ix_BA"]);
        Assert.Equal(["A", "B"], Rows(sqlite, "PRAGMA index_info(\"T_ix_AB\")").OrderBy(r => Convert.ToInt32(r[0], CultureInfo.InvariantCulture)).Select(r => (string)r[2]!));
        Assert.Equal(["B", "A"], Rows(sqlite, "PRAGMA index_info(\"T_ix_BA\")").OrderBy(r => Convert.ToInt32(r[0], CultureInfo.InvariantCulture)).Select(r => (string)r[2]!));
    }

    [Fact]
    public void Build_ForeignKeyChildIndex_IsCreatedWhenNoIndexStartsWithTheColumn()
    {
        var catalog = Make(
            [Simple("Parent"), Simple("Child", "ParentId", "Other")],
            [Fk("fk_child_parent", "Child", "ParentId", "Parent")]);

        var ddl = NativeSchemaBuilder.Build(catalog);
        using var sqlite = Run(ddl);

        Assert.Equal(["Child_FK_ParentId"], ddl.ForeignKeyIndexNames);
        var index = Assert.Single(Rows(sqlite, "PRAGMA index_list(\"Child\")"), r => (string)r[3]! == "c");
        Assert.Equal("Child_FK_ParentId", index[1]);
        Assert.Equal(0, Convert.ToInt32(index[2], CultureInfo.InvariantCulture));
        Assert.Equal(["ParentId"], Rows(sqlite, "PRAGMA index_info(\"Child_FK_ParentId\")").Select(r => (string)r[2]!));
    }

    [Fact]
    public void Build_ForeignKeyChildIndex_IsNotCreatedWhenAnAccessIndexStartsWithTheColumn()
    {
        // Negative case: the Access index (ParentId, Other) starts with the child column.
        var catalog = Make(
            [Simple("Parent"), Simple("Child", "ParentId", "Other")],
            [Fk("fk_child_parent", "Child", "ParentId", "Parent")],
            [Index("Child", "ix_po", unique: false, "ParentId", "Other")]);

        var ddl = NativeSchemaBuilder.Build(catalog);

        Assert.Empty(ddl.ForeignKeyIndexNames);
        Assert.Equal(["Child_ix_po"], ddl.AccessIndexNames);
        Assert.DoesNotContain(ddl.IndexStatements, s => s.Contains("_FK_", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ForeignKeyChildIndex_IsNotCreatedWhenThePrimaryKeyStartsWithTheColumn()
    {
        var catalog = Make(
            [Simple("Parent"), Table("Child", ["ParentId", "Seq"], Col("ParentId", nullable: false), Col("Seq", nullable: false))],
            [Fk("fk_child_parent", "Child", "ParentId", "Parent")]);

        var ddl = NativeSchemaBuilder.Build(catalog);

        Assert.Empty(ddl.ForeignKeyIndexNames);
        Assert.Empty(ddl.IndexStatements);
    }

    [Fact]
    public void Build_ForeignKeyChildIndex_IsCreatedWhenTheColumnIsOnlyASecondIndexColumn()
    {
        // An index (Other, ParentId) does not serve a join on ParentId: the FK index is still needed.
        var catalog = Make(
            [Simple("Parent"), Simple("Child", "ParentId", "Other")],
            [Fk("fk_child_parent", "Child", "ParentId", "Parent")],
            [Index("Child", "ix_op", unique: false, "Other", "ParentId")]);

        var ddl = NativeSchemaBuilder.Build(catalog);

        Assert.Equal(["Child_FK_ParentId"], ddl.ForeignKeyIndexNames);
    }

    [Fact]
    public void Build_ForeignKeyChildIndex_OfADroppedForeignKey_IsNotCreated()
    {
        var catalog = Make(
            [Simple("ChangeLog"), Simple("Child", "LogId")],
            [Fk("fk_to_excluded", "Child", "LogId", "ChangeLog")]);

        var ddl = NativeSchemaBuilder.Build(catalog);

        Assert.Empty(ddl.IndexStatements);
    }

    // ------------------------------------------------------------------ determinism and errors

    [Fact]
    public void Build_IsDeterministic_SameCatalogueTwice_IdenticalText()
    {
        var baseline = NativeBaseline.Load().Catalog;

        var first = NativeSchemaBuilder.Build(baseline);
        var second = NativeSchemaBuilder.Build(baseline);

        Assert.Equal(first.Statements, second.Statements);
        Assert.Equal(string.Join("\n", first.Statements), string.Join("\n", second.Statements));
    }

    [Fact]
    public void Build_TablesAreInOrdinalNameOrder_WhateverTheOrderOfTheInput()
    {
        var baseline = NativeBaseline.Load().Catalog;
        var shuffled = new NativeCatalog(
            [.. baseline.Tables.Reverse()], [.. baseline.ForeignKeys.Reverse()], [.. baseline.Indexes.Reverse()]);

        var ddl = NativeSchemaBuilder.Build(shuffled);

        Assert.Equal(NativeSchemaBuilder.Build(baseline).Statements, ddl.Statements);
        var names = ddl.Tables.Select(t => t.Name).ToList();
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
    }

    [Fact]
    public void Build_Determinism_PositiveControl_ADifferentCatalogueGivesDifferentText()
    {
        var baseline = NativeBaseline.Load().Catalog;
        var table = baseline.Tables.First(t => !NativeExclusions.IsExcluded(t.Name));
        var changed = new NativeCatalog(
            [.. baseline.Tables.Select(t => t == table ? t with { Columns = [.. t.Columns.Skip(1)] } : t)],
            baseline.ForeignKeys, baseline.Indexes);

        Assert.NotEqual(
            string.Join("\n", NativeSchemaBuilder.Build(baseline).Statements),
            string.Join("\n", NativeSchemaBuilder.Build(changed).Statements));
    }

    [Fact]
    public void Build_UnmappedColumnType_FailsNamingTableAndColumn()
    {
        var catalog = Make([Table("Money", ["Id"], Col("Id", nullable: false), Col("Amount", 6))]);

        var ex = Assert.Throws<NotSupportedException>(() => NativeSchemaBuilder.Build(catalog));

        Assert.Contains("Money.Amount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UnmappedColumnInAnExcludedTable_DoesNotStopTheBuild()
    {
        // The excluded tables are not exported, so their column types are not needed.
        var catalog = Make(
            [Table("ChangeLog", ["Id"], Col("Id", nullable: false), Col("Timestamp", 6)), Simple("Kept")]);

        var ddl = NativeSchemaBuilder.Build(catalog);

        Assert.Equal(["Kept"], ddl.Tables.Select(t => t.Name));
    }

    [Fact]
    public void Build_UnknownRelationshipRule_Fails()
    {
        var catalog = Make(
            [Simple("Parent"), Simple("Child", "P")],
            [Fk("fk", "Child", "P", "Parent", update: "RESTRICT-ISH")]);

        Assert.Throws<InvalidOperationException>(() => NativeSchemaBuilder.Build(catalog));
    }

    // ------------------------------------------------------------------ the 4.4 baseline

    [Fact]
    public void BaselineDdl_Has59Tables_170ForeignKeysDeclared_8NotDeclared_AndRunsInSqlite()
    {
        var baseline = NativeBaseline.Load();
        Assert.Equal("4.4", baseline.ReleaseCode);

        var ddl = NativeSchemaBuilder.Build(baseline.Catalog);
        using var sqlite = Run(ddl);

        Assert.Equal(59, ddl.Tables.Count);
        Assert.Equal(59, AccessSqliteComparer.SqliteUserTables(sqlite).Count);
        Assert.Equal(170, ddl.ForeignKeysDeclared.Count);
        Assert.Equal(8, ddl.ForeignKeysDropped.Count);
        Assert.Equal(
            ddl.ForeignKeysDeclared.Count,
            ddl.Tables.Sum(t => Rows(sqlite, $"PRAGMA foreign_key_list({AccessSqliteComparer.Q(t.Name)})").Count));
        Assert.Empty(Rows(sqlite, "PRAGMA foreign_key_check"));
        foreach (var excluded in NativeExclusions.ExcludedTables)
        {
            Assert.DoesNotContain(excluded, AccessSqliteComparer.SqliteUserTables(sqlite));
        }

        // Every exported table has a primary key (spec: on 4.4 every exported table has one).
        Assert.All(ddl.Tables, t => Assert.NotEmpty(t.PrimaryKey));
        // The composite keys that SQLite sees are those of the catalogue (the documentation says 19;
        // the baseline holds 18 among the exported tables: reported, see the data tests).
        Assert.Equal(
            ddl.Tables.Count(t => t.PrimaryKey.Count > 1),
            ddl.Tables.Count(t => Rows(sqlite, $"PRAGMA table_info({AccessSqliteComparer.Q(t.Name)})")
                .Count(r => Convert.ToInt32(r[5], CultureInfo.InvariantCulture) > 0) > 1));
    }
}
