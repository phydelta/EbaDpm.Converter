using EbaDpm.Converter.Core.Access.Dpm20;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// In-memory <see cref="IDpm20AxisAndCellSource"/>: returns the rows it was given, honouring the
/// id filters of the bounded reads like the Access reader does. No ACE, no data files.
/// </summary>
internal sealed class InMemoryAxisAndCellSource : IDpm20AxisAndCellSource
{
    public int CutoffReleaseId { get; init; }

    public List<Dpm20SubCategoryVersionRow> SubCategoryVersions { get; } = [];
    public List<Dpm20TableVersionHeaderRow> TableVersionHeaders { get; } = [];
    public List<Dpm20HeaderRow> Headers { get; } = [];
    public List<Dpm20HeaderVersionRow> HeaderVersions { get; } = [];
    public List<Dpm20CellRow> Cells { get; } = [];
    public List<Dpm20TableVersionCellRow> TableVersionCells { get; } = [];
    public List<Dpm20VariableVersionRow> VariableVersions { get; } = [];
    public List<Dpm20ContextCompositionRow> ContextCompositions { get; } = [];
    public List<Dpm20CategoryRow> Categories { get; } = [];
    public List<Dpm20ItemCategoryRow> ItemCategories { get; } = [];
    public List<Dpm20PropertyCategoryRow> PropertyCategories { get; } = [];
    public List<Dpm20DataTypeRow> DataTypes { get; } = [];
    public List<Dpm20PropertyRow> Properties { get; } = [];

    public IEnumerable<Dpm20SubCategoryVersionRow> ReadSubCategoryVersions() => SubCategoryVersions;

    public IEnumerable<Dpm20SubCategoryVersionRow> ReadAllSubCategoryVersions() => SubCategoryVersions;

    public IEnumerable<Dpm20TableVersionHeaderRow> ReadTableVersionHeaders(IEnumerable<int> tableVIds)
    {
        var ids = tableVIds.ToHashSet();
        return TableVersionHeaders.Where(r => ids.Contains(r.TableVId)).ToList();
    }

    public IEnumerable<Dpm20HeaderRow> ReadHeadersByTableIds(IEnumerable<int> tableIds)
    {
        var ids = tableIds.ToHashSet();
        return Headers.Where(r => ids.Contains(r.TableId)).ToList();
    }

    public IEnumerable<Dpm20HeaderVersionRow> ReadHeaderVersionsByIds(IEnumerable<int> headerVIds)
    {
        var ids = headerVIds.ToHashSet();
        return HeaderVersions.Where(r => ids.Contains(r.HeaderVId)).ToList();
    }

    public IEnumerable<Dpm20CellRow> ReadCellsByTableIds(IEnumerable<int> tableIds)
    {
        var ids = tableIds.ToHashSet();
        return Cells.Where(r => ids.Contains(r.TableId)).ToList();
    }

    public IEnumerable<Dpm20TableVersionCellRow> ReadTableVersionCellsByTableVIds(IEnumerable<int> tableVIds)
    {
        var ids = tableVIds.ToHashSet();
        return TableVersionCells.Where(r => ids.Contains(r.TableVId)).ToList();
    }

    public IEnumerable<Dpm20VariableVersionRow> ReadVariableVersionsByVariableVIds(IEnumerable<int> variableVIds)
    {
        var ids = variableVIds.ToHashSet();
        return VariableVersions.Where(r => ids.Contains(r.VariableVId)).ToList();
    }

    public IEnumerable<Dpm20ContextCompositionRow> ReadContextCompositionsByContextIds(IEnumerable<int> contextIds)
    {
        var ids = contextIds.ToHashSet();
        return ContextCompositions.Where(r => ids.Contains(r.ContextId)).ToList();
    }

    public IEnumerable<Dpm20CategoryRow> ReadCategories() => Categories;

    public IEnumerable<Dpm20ItemCategoryRow> ReadItemCategories() => ItemCategories;

    public IEnumerable<Dpm20PropertyCategoryRow> ReadPropertyCategories() => PropertyCategories;

    public IEnumerable<Dpm20DataTypeRow> ReadDataTypes() => DataTypes;

    public IEnumerable<Dpm20PropertyRow> ReadProperties() => Properties;
}
