namespace EbaDpm.Converter.Core.Access.Dpm20;

/// <summary>
/// The part of the DPM 2.0 source that <c>Dpm20HierarchyNodeLoader.Load</c> reads.
/// <see cref="Dpm20AccessReader"/> is the production implementation; the interface exists so that
/// the loader can run from in-memory rows. It declares nothing else: it is a seam, not a general
/// abstraction of the reader.
/// </summary>
public interface IDpm20HierarchyNodeSource
{
    /// <inheritdoc cref="Dpm20AccessReader.CutoffReleaseId"/>
    int CutoffReleaseId { get; }

    /// <inheritdoc cref="Dpm20AccessReader.ReadAllSubCategoryVersions"/>
    IEnumerable<Dpm20SubCategoryVersionRow> ReadAllSubCategoryVersions();

    /// <inheritdoc cref="Dpm20AccessReader.ReadSubCategoryItems"/>
    IEnumerable<Dpm20SubCategoryItemRow> ReadSubCategoryItems();

    /// <inheritdoc cref="Dpm20AccessReader.ReadItemCategories"/>
    IEnumerable<Dpm20ItemCategoryRow> ReadItemCategories();

    /// <inheritdoc cref="Dpm20AccessReader.ReadCategories"/>
    IEnumerable<Dpm20CategoryRow> ReadCategories();

    /// <inheritdoc cref="Dpm20AccessReader.ReadItems"/>
    IEnumerable<Dpm20ItemRow> ReadItems();

    /// <inheritdoc cref="Dpm20AccessReader.ReadOperators"/>
    IEnumerable<Dpm20OperatorRow> ReadOperators();
}
