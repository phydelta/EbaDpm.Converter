namespace EbaDpm.Converter.Native.Catalog;

/// <summary>
/// The catalogue of an Access database as the native output sees it: user tables with their
/// columns and primary keys, relationships (foreign keys) and non-primary-key indexes. All the
/// records have value equality (the lists are compared element by element), so two catalogues
/// are equal exactly when they declare the same schema; <see cref="NativeCatalogComparer"/>
/// explains the differences.
///
/// The collections are in a canonical order (tables by ordinal name, columns by Access ordinal
/// position, foreign keys by child table and name, indexes by table and name), so that the
/// serialized form is stable.
/// </summary>
public sealed record NativeCatalog(
    IReadOnlyList<NativeTable> Tables,
    IReadOnlyList<NativeForeignKey> ForeignKeys,
    IReadOnlyList<NativeIndex> Indexes)
{
    public bool Equals(NativeCatalog? other)
        => other is not null
            && SequenceEquality.Equal(Tables, other.Tables)
            && SequenceEquality.Equal(ForeignKeys, other.ForeignKeys)
            && SequenceEquality.Equal(Indexes, other.Indexes);

    public override int GetHashCode()
        => HashCode.Combine(SequenceEquality.Hash(Tables), SequenceEquality.Hash(ForeignKeys), SequenceEquality.Hash(Indexes));
}

/// <summary>A user table. <see cref="Columns"/> are in Access ordinal order; <see cref="PrimaryKey"/> in key order.</summary>
public sealed record NativeTable(string Name, IReadOnlyList<NativeColumn> Columns, IReadOnlyList<string> PrimaryKey)
{
    public bool Equals(NativeTable? other)
        => other is not null
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && SequenceEquality.Equal(Columns, other.Columns)
            && SequenceEquality.Equal(PrimaryKey, other.PrimaryKey);

    public override int GetHashCode()
        => HashCode.Combine(Name, SequenceEquality.Hash(Columns), SequenceEquality.Hash(PrimaryKey));
}

/// <summary>
/// A column. <see cref="DataType"/> is the OLE DB data type code (<c>DBTYPE</c>: 3 = adInteger,
/// 130 = adWChar...), exactly as <c>OleDbSchemaGuid.Columns</c> reports it.
/// </summary>
public sealed record NativeColumn(string Name, int DataType, bool Nullable);

/// <summary>
/// An Access relationship. The column lists have the same length and are in key order (one
/// element each on every relationship of the 4.4 database). The rules are the Access ones
/// (<c>CASCADE</c>, <c>NO ACTION</c>, <c>SET NULL</c>, <c>SET DEFAULT</c>).
/// </summary>
public sealed record NativeForeignKey(
    string Name,
    string ChildTable,
    IReadOnlyList<string> ChildColumns,
    string ParentTable,
    IReadOnlyList<string> ParentColumns,
    string UpdateRule,
    string DeleteRule)
{
    public bool Equals(NativeForeignKey? other)
        => other is not null
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && string.Equals(ChildTable, other.ChildTable, StringComparison.Ordinal)
            && SequenceEquality.Equal(ChildColumns, other.ChildColumns)
            && string.Equals(ParentTable, other.ParentTable, StringComparison.Ordinal)
            && SequenceEquality.Equal(ParentColumns, other.ParentColumns)
            && string.Equals(UpdateRule, other.UpdateRule, StringComparison.Ordinal)
            && string.Equals(DeleteRule, other.DeleteRule, StringComparison.Ordinal);

    public override int GetHashCode()
        => HashCode.Combine(
            Name, ChildTable, SequenceEquality.Hash(ChildColumns), ParentTable, SequenceEquality.Hash(ParentColumns), UpdateRule, DeleteRule);
}

/// <summary>A non-primary-key index of Access, with its columns in index order.</summary>
public sealed record NativeIndex(string Table, string Name, IReadOnlyList<string> Columns, bool Unique)
{
    public bool Equals(NativeIndex? other)
        => other is not null
            && string.Equals(Table, other.Table, StringComparison.Ordinal)
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && SequenceEquality.Equal(Columns, other.Columns)
            && Unique == other.Unique;

    public override int GetHashCode()
        => HashCode.Combine(Table, Name, SequenceEquality.Hash(Columns), Unique);
}

/// <summary>Element-wise equality of the lists the catalogue records hold.</summary>
internal static class SequenceEquality
{
    public static bool Equal<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
        => left.SequenceEqual(right);

    public static int Hash<T>(IReadOnlyList<T> list)
    {
        var hash = new HashCode();
        foreach (var item in list)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}
