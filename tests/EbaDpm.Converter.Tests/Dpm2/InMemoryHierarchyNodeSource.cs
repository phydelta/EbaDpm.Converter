using EbaDpm.Converter.Core.Access.Dpm20;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// In-memory <see cref="IDpm20HierarchyNodeSource"/>: returns the rows it was given, empty by default.
/// No ACE, no data files.
/// </summary>
internal sealed class InMemoryHierarchyNodeSource : IDpm20HierarchyNodeSource
{
    public int CutoffReleaseId { get; set; }
    public List<Dpm20SubCategoryVersionRow> SubCategoryVersions { get; } = [];
    public List<Dpm20SubCategoryItemRow> SubCategoryItems { get; } = [];
    public List<Dpm20ItemCategoryRow> ItemCategories { get; } = [];
    public List<Dpm20CategoryRow> Categories { get; } = [];
    public List<Dpm20ItemRow> Items { get; } = [];
    public List<Dpm20OperatorRow> Operators { get; } = [];

    public IEnumerable<Dpm20SubCategoryVersionRow> ReadAllSubCategoryVersions() => SubCategoryVersions;

    public IEnumerable<Dpm20SubCategoryItemRow> ReadSubCategoryItems() => SubCategoryItems;

    public IEnumerable<Dpm20ItemCategoryRow> ReadItemCategories() => ItemCategories;

    public IEnumerable<Dpm20CategoryRow> ReadCategories() => Categories;

    public IEnumerable<Dpm20ItemRow> ReadItems() => Items;

    public IEnumerable<Dpm20OperatorRow> ReadOperators() => Operators;
}
