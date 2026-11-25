using EbaDpm.Converter.Native.Catalog;

namespace EbaDpm.Converter.Native.Tests.Support;

/// <summary>Terse builders of synthetic <see cref="NativeCatalog"/> values for the schema tests.</summary>
internal static class Cat
{
    public const int Int = 3;
    public const int Text = 130;
    public const int Bool = 11;

    public static NativeColumn Col(string name, int type = Int, bool nullable = true) => new(name, type, nullable);

    public static NativeTable Table(string name, string[] primaryKey, params NativeColumn[] columns)
        => new(name, columns, primaryKey);

    /// <summary>A table with an integer, required <c>Id</c> primary key plus the given nullable integer columns.</summary>
    public static NativeTable Simple(string name, params string[] extraColumns)
        => Table(name, ["Id"], [Col("Id", nullable: false), .. extraColumns.Select(c => Col(c))]);

    public static NativeForeignKey Fk(
        string name, string child, string childColumn, string parent, string parentColumn = "Id",
        string update = "CASCADE", string delete = "NO ACTION")
        => new(name, child, [childColumn], parent, [parentColumn], update, delete);

    public static NativeIndex Index(string table, string name, bool unique, params string[] columns)
        => new(table, name, columns, unique);

    public static NativeCatalog Make(
        IEnumerable<NativeTable> tables, IEnumerable<NativeForeignKey>? foreignKeys = null, IEnumerable<NativeIndex>? indexes = null)
        => new([.. tables], [.. foreignKeys ?? []], [.. indexes ?? []]);
}
