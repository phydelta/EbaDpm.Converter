namespace EbaDpm.Converter.Core.Access.Dpm20;

/// <summary>
/// The part of the DPM 2.0 source that <c>Dpm20AxisAndCellLoader.Load</c> and
/// <c>DimensionMemberResolver.Build</c> read. <see cref="Dpm20AccessReader"/> is the production
/// implementation; the interface exists so that both can run from in-memory rows. It declares
/// nothing else: it is a seam, not a general abstraction of the reader.
/// </summary>
public interface IDpm20AxisAndCellSource
{
    /// <inheritdoc cref="Dpm20AccessReader.CutoffReleaseId"/>
    int CutoffReleaseId { get; }

    /// <inheritdoc cref="Dpm20AccessReader.ReadSubCategoryVersions"/>
    IEnumerable<Dpm20SubCategoryVersionRow> ReadSubCategoryVersions();

    /// <inheritdoc cref="Dpm20AccessReader.ReadAllSubCategoryVersions"/>
    IEnumerable<Dpm20SubCategoryVersionRow> ReadAllSubCategoryVersions();

    /// <inheritdoc cref="Dpm20AccessReader.ReadTableVersionHeaders"/>
    IEnumerable<Dpm20TableVersionHeaderRow> ReadTableVersionHeaders(IEnumerable<int> tableVIds);

    /// <inheritdoc cref="Dpm20AccessReader.ReadHeadersByTableIds"/>
    IEnumerable<Dpm20HeaderRow> ReadHeadersByTableIds(IEnumerable<int> tableIds);

    /// <inheritdoc cref="Dpm20AccessReader.ReadHeaderVersionsByIds"/>
    IEnumerable<Dpm20HeaderVersionRow> ReadHeaderVersionsByIds(IEnumerable<int> headerVIds);

    /// <inheritdoc cref="Dpm20AccessReader.ReadCellsByTableIds"/>
    IEnumerable<Dpm20CellRow> ReadCellsByTableIds(IEnumerable<int> tableIds);

    /// <inheritdoc cref="Dpm20AccessReader.ReadTableVersionCellsByTableVIds"/>
    IEnumerable<Dpm20TableVersionCellRow> ReadTableVersionCellsByTableVIds(IEnumerable<int> tableVIds);

    /// <inheritdoc cref="Dpm20AccessReader.ReadVariableVersionsByVariableVIds"/>
    IEnumerable<Dpm20VariableVersionRow> ReadVariableVersionsByVariableVIds(IEnumerable<int> variableVIds);

    /// <inheritdoc cref="Dpm20AccessReader.ReadContextCompositionsByContextIds"/>
    IEnumerable<Dpm20ContextCompositionRow> ReadContextCompositionsByContextIds(IEnumerable<int> contextIds);

    /// <inheritdoc cref="Dpm20AccessReader.ReadCategories"/>
    IEnumerable<Dpm20CategoryRow> ReadCategories();

    /// <inheritdoc cref="Dpm20AccessReader.ReadItemCategories"/>
    IEnumerable<Dpm20ItemCategoryRow> ReadItemCategories();

    /// <inheritdoc cref="Dpm20AccessReader.ReadPropertyCategories"/>
    IEnumerable<Dpm20PropertyCategoryRow> ReadPropertyCategories();

    /// <inheritdoc cref="Dpm20AccessReader.ReadDataTypes"/>
    IEnumerable<Dpm20DataTypeRow> ReadDataTypes();

    /// <inheritdoc cref="Dpm20AccessReader.ReadProperties"/>
    IEnumerable<Dpm20PropertyRow> ReadProperties();
}
