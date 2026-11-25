using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Native.Catalog;
using EbaDpm.Converter.Native.Tests.Support;
using EbaDpm.Converter.Native.Validation;

namespace EbaDpm.Converter.Native.Tests.Validation;

/// <summary>
/// The schema drift report (<see cref="NativeDrift"/>, <c>A-DRF-01</c>): the source catalogue against the
/// embedded 4.4 baseline. Synthetic catalogues are the baseline modified in one known way; each
/// modification must be reported, with its kind and its business name, and the unmodified baseline must
/// report nothing (the negative control that makes the non-empty results meaningful).
/// </summary>
public sealed class NativeDriftTests(NativeValidationFixture fixture) : IClassFixture<NativeValidationFixture>
{
    private static NativeCatalog Baseline => NativeBaseline.Load().Catalog;

    /// <summary>A table with a non-key column that is not part of any other structure of interest.</summary>
    private static NativeTable Subject(NativeCatalog catalog)
        => catalog.Tables.First(t => t.Columns.Count >= 3 && t.PrimaryKey.Count == 1 && t.Columns.Any(c => !t.PrimaryKey.Contains(c.Name) && c.Nullable));

    private static NativeCatalog Replace(NativeCatalog catalog, NativeTable table, NativeTable? with)
    {
        var tables = catalog.Tables.Where(t => t.Name != table.Name).ToList();
        if (with is not null)
        {
            tables.Add(with);
        }

        return catalog with { Tables = [.. tables.OrderBy(t => t.Name, StringComparer.Ordinal)] };
    }

    private static NativeCatalogDifference Single(IReadOnlyList<NativeCatalogDifference> drift, NativeCatalogDifferenceKind kind, string subject)
        => Assert.Single(drift, d => d.Kind == kind && d.Subject == subject);

    [Fact]
    public void Baseline_AgainstItself_HasNoDrift()
    {
        Assert.Empty(NativeDrift.Compute(Baseline));
    }

    [Fact]
    public void Comparer_IsSymmetric_AddedInOneDirectionIsRemovedInTheOther()
    {
        var table = Subject(Baseline);
        var modified = Replace(Baseline, table, table with { Columns = [.. table.Columns, new NativeColumn("ZzNewColumn", 3, true)] });

        var forward = NativeCatalogComparer.Compare(Baseline, modified);
        var backward = NativeCatalogComparer.Compare(modified, Baseline);

        Assert.Equal(NativeCatalogDifferenceKind.ColumnAdded, Assert.Single(forward).Kind);
        Assert.Equal(NativeCatalogDifferenceKind.ColumnRemoved, Assert.Single(backward).Kind);
        Assert.Equal(forward[0].Subject, backward[0].Subject);
        Assert.Empty(NativeCatalogComparer.Compare(modified, modified));
    }

    [Fact]
    public void NullCatalogue_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => NativeDrift.Compute(null!));
    }

    [Fact]
    public void TableAdded_IsReported_WithItsName()
    {
        var added = new NativeTable("ZzNewTable", [new NativeColumn("ZzNewTableID", 3, false)], ["ZzNewTableID"]);
        var catalog = Baseline with { Tables = [.. Baseline.Tables, added] };

        var drift = NativeDrift.Compute(catalog);

        var difference = Assert.Single(drift);
        Assert.Equal(NativeCatalogDifferenceKind.TableAdded, difference.Kind);
        Assert.Equal("ZzNewTable", difference.Subject);
        Assert.Null(difference.Baseline);
        Assert.NotNull(difference.Actual);
    }

    [Fact]
    public void TableRemoved_IsReported_WithItsName()
    {
        var removed = Baseline.Tables.First(t => !Baseline.ForeignKeys.Any(f => f.ChildTable == t.Name || f.ParentTable == t.Name)
            && !Baseline.Indexes.Any(i => i.Table == t.Name));
        var catalog = Replace(Baseline, removed, null);

        var drift = NativeDrift.Compute(catalog);

        var difference = Assert.Single(drift);
        Assert.Equal(NativeCatalogDifferenceKind.TableRemoved, difference.Kind);
        Assert.Equal(removed.Name, difference.Subject);
        Assert.NotNull(difference.Baseline);
        Assert.Null(difference.Actual);
    }

    [Fact]
    public void ColumnAdded_IsReported_AsTableDotColumn()
    {
        var table = Subject(Baseline);
        var catalog = Replace(Baseline, table, table with { Columns = [.. table.Columns, new NativeColumn("ZzNewColumn", 130, true)] });

        var drift = NativeDrift.Compute(catalog);

        var difference = Assert.Single(drift);
        Assert.Equal(NativeCatalogDifferenceKind.ColumnAdded, difference.Kind);
        Assert.Equal(table.Name + ".ZzNewColumn", difference.Subject);
        Assert.Null(difference.Baseline);
        Assert.Contains("130", difference.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnRemoved_IsReported_AsTableDotColumn()
    {
        var table = Subject(Baseline);
        var column = table.Columns.First(c => !table.PrimaryKey.Contains(c.Name) && c.Nullable);
        var catalog = Replace(Baseline, table, table with { Columns = [.. table.Columns.Where(c => c.Name != column.Name)] });

        var drift = NativeDrift.Compute(catalog);

        var difference = Assert.Single(drift);
        Assert.Equal(NativeCatalogDifferenceKind.ColumnRemoved, difference.Kind);
        Assert.Equal(table.Name + "." + column.Name, difference.Subject);
        Assert.NotNull(difference.Baseline);
        Assert.Null(difference.Actual);
    }

    [Fact]
    public void ColumnRetyped_IsReported_WithBothTypeCodes()
    {
        var table = Subject(Baseline);
        var column = table.Columns.First(c => !table.PrimaryKey.Contains(c.Name) && c.Nullable);
        var retyped = column with { DataType = column.DataType == 3 ? 130 : 3 };
        var catalog = Replace(Baseline, table, table with { Columns = [.. table.Columns.Select(c => c.Name == column.Name ? retyped : c)] });

        var drift = NativeDrift.Compute(catalog);

        var difference = Single(drift, NativeCatalogDifferenceKind.ColumnTypeChanged, table.Name + "." + column.Name);
        Assert.Equal(column.DataType.ToString(System.Globalization.CultureInfo.InvariantCulture), difference.Baseline);
        Assert.Equal(retyped.DataType.ToString(System.Globalization.CultureInfo.InvariantCulture), difference.Actual);
        Assert.Single(drift);
    }

    [Fact]
    public void ColumnNullabilityChange_IsReported()
    {
        var table = Subject(Baseline);
        var column = table.Columns.First(c => !table.PrimaryKey.Contains(c.Name) && c.Nullable);
        var catalog = Replace(Baseline, table, table with { Columns = [.. table.Columns.Select(c => c.Name == column.Name ? c with { Nullable = false } : c)] });

        var difference = Single(NativeDrift.Compute(catalog), NativeCatalogDifferenceKind.ColumnNullabilityChanged, table.Name + "." + column.Name);

        Assert.Equal("NULL", difference.Baseline);
        Assert.Equal("NOT NULL", difference.Actual);
    }

    [Fact]
    public void ColumnReorder_IsReported_WithoutAdditionsCountingAsReorder()
    {
        var table = Subject(Baseline);
        var reordered = table with { Columns = [.. table.Columns.Reverse()] };

        var drift = NativeDrift.Compute(Replace(Baseline, table, reordered));

        var difference = Assert.Single(drift);
        Assert.Equal(NativeCatalogDifferenceKind.ColumnOrderChanged, difference.Kind);
        Assert.Equal(table.Name, difference.Subject);
    }

    [Fact]
    public void ForeignKeyAndIndexChanges_AreReported()
    {
        var fk = Baseline.ForeignKeys[0];
        var withoutFk = Baseline with { ForeignKeys = [.. Baseline.ForeignKeys.Skip(1)] };
        Single(NativeDrift.Compute(withoutFk), NativeCatalogDifferenceKind.ForeignKeyRemoved, fk.ChildTable + "." + fk.Name);

        var changedFk = Baseline with { ForeignKeys = [fk with { DeleteRule = fk.DeleteRule == "CASCADE" ? "NO ACTION" : "CASCADE" }, .. Baseline.ForeignKeys.Skip(1)] };
        Single(NativeDrift.Compute(changedFk), NativeCatalogDifferenceKind.ForeignKeyChanged, fk.ChildTable + "." + fk.Name);

        var index = Baseline.Indexes[0];
        var withoutIndex = Baseline with { Indexes = [.. Baseline.Indexes.Skip(1)] };
        Single(NativeDrift.Compute(withoutIndex), NativeCatalogDifferenceKind.IndexRemoved, index.Table + "." + index.Name);

        var extraIndex = Baseline with { Indexes = [.. Baseline.Indexes, new NativeIndex(index.Table, "ZzIndex", index.Columns, false)] };
        Single(NativeDrift.Compute(extraIndex), NativeCatalogDifferenceKind.IndexAdded, index.Table + ".ZzIndex");
    }

    [Fact]
    public void PrimaryKeyChange_IsReported()
    {
        var table = Subject(Baseline);
        var other = table.Columns.First(c => !table.PrimaryKey.Contains(c.Name)).Name;

        var drift = NativeDrift.Compute(Replace(Baseline, table, table with { PrimaryKey = [.. table.PrimaryKey, other] }));

        Single(drift, NativeCatalogDifferenceKind.PrimaryKeyChanged, table.Name);
    }

    [Fact]
    public void SeveralDifferences_AreAllReported_InADeterministicOrder()
    {
        var table = Subject(Baseline);
        var added = new NativeTable("ZzNewTable", [new NativeColumn("ZzNewTableID", 3, false)], ["ZzNewTableID"]);
        var catalog = Replace(Baseline, table, table with { Columns = [.. table.Columns, new NativeColumn("ZzNewColumn", 3, true)] });
        catalog = catalog with { Tables = [.. catalog.Tables, added] };

        var first = NativeDrift.Compute(catalog);
        var second = NativeDrift.Compute(catalog);

        Assert.Equal(2, first.Count);
        Assert.Equal(first, second);
        Single(first, NativeCatalogDifferenceKind.TableAdded, "ZzNewTable");
        Single(first, NativeCatalogDifferenceKind.ColumnAdded, table.Name + ".ZzNewColumn");
    }

    // ------------------------------------------------------------------ on a real Access database (synthetic)

    [AceFact]
    public void SyntheticSource_DriftNamesTheTablesAndColumnsThatDiffer_AndNeverFailsTheValidation()
    {
        var catalog = NativeCatalogReader.Read(fixture.SourcePath);
        NativeValidationFixture.ReleasePools();
        var full = NativeDrift.Compute(catalog);
        var keys = full.Select(d => $"{d.Kind} {d.Subject}").ToList();

        Assert.Contains("TableAdded TypeProbe", keys);                        // in the source, not in 4.4
        Assert.Contains("TableAdded Composite", keys);
        Assert.Contains("ColumnAdded Category.ExtraCol", keys);               // column added to a table both have
        Assert.Contains("ColumnRemoved Category.Description", keys);          // column of 4.4 the source lacks
        Assert.Contains("ColumnTypeChanged TableVersionCell.CellCode", keys); // 130 in 4.4, 3 in the source
        Assert.Contains(keys, k => k.StartsWith("TableRemoved ", StringComparison.Ordinal)); // 4.4 tables the source lacks
        var retyped = Assert.Single(full, d => d.Kind == NativeCatalogDifferenceKind.ColumnTypeChanged && d.Subject == "TableVersionCell.CellCode");
        Assert.Equal("130", retyped.Baseline);
        Assert.Equal("3", retyped.Actual);

        // The validation reports the same list (the first 50 as samples, the real count in totalFailed), informative, exit 0.
        var report = NativeValidator.Validate(fixture.OutputPath, fixture.SourcePath);
        NativeValidationFixture.ReleasePools();
        var drift = Assert.Single(report.Checks, c => c.Id == "A-DRF-01");
        Assert.Equal("info", drift.Status);
        Assert.Equal("informative", drift.Layer);
        Assert.Equal(full.Count, drift.Failed);
        Assert.True(full.Count > CheckResult.MaxSamples, "the synthetic drift is expected to exceed the sample cap");
        Assert.Equal(CheckResult.MaxSamples, drift.Samples.Count);
        Assert.Equal(full.Count, drift.TotalFailed);
        Assert.Equal(
            full.Take(CheckResult.MaxSamples).Select(d => $"{d.Kind} {d.Subject}"),
            drift.Samples.Select(s => s.BusinessKey));
        Assert.Equal(0, report.Summary.ExitCode);
        Assert.Equal(0, report.Summary.Critical.Failed);
    }
}
