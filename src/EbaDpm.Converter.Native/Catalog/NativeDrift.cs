namespace EbaDpm.Converter.Native.Catalog;

/// <summary>
/// The schema drift of a source: how its catalogue differs from the embedded 4.4 baseline (tables
/// and columns added, removed or retyped, primary keys, relationships, indexes). Informative: it
/// never fails a conversion or a validation. One computation shared by the conversion summary and
/// <c>--validate</c>.
/// </summary>
public static class NativeDrift
{
    public static IReadOnlyList<NativeCatalogDifference> Compute(NativeCatalog source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return NativeCatalogComparer.Compare(NativeBaseline.Load().Catalog, source);
    }
}
