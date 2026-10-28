namespace EbaDpm.Converter.Core.Access.Dpm20;

/// <summary>
/// The part of the DPM 2.0 source that <c>Dpm20DictionaryLoader.Load</c> reads.
/// <see cref="Dpm20AccessReader"/> is the production implementation; the interface exists so that
/// the loader can run from in-memory rows. It declares nothing else: it is a seam, not a general
/// abstraction of the reader.
/// </summary>
public interface IDpm20DictionarySource
{
    /// <inheritdoc cref="Dpm20AccessReader.ReadDataTypes"/>
    IEnumerable<Dpm20DataTypeRow> ReadDataTypes();

    /// <inheritdoc cref="Dpm20AccessReader.ReadProperties"/>
    IEnumerable<Dpm20PropertyRow> ReadProperties();

    /// <inheritdoc cref="Dpm20AccessReader.ReadPropertyCategories"/>
    IEnumerable<Dpm20PropertyCategoryRow> ReadPropertyCategories();

    /// <inheritdoc cref="Dpm20AccessReader.ReadCategories"/>
    IEnumerable<Dpm20CategoryRow> ReadCategories();

    /// <inheritdoc cref="Dpm20AccessReader.ReadSuperCategoryCompositions"/>
    IEnumerable<Dpm20SuperCategoryCompositionRow> ReadSuperCategoryCompositions();

    /// <inheritdoc cref="Dpm20AccessReader.ReadItemCategories"/>
    IEnumerable<Dpm20ItemCategoryRow> ReadItemCategories();

    /// <inheritdoc cref="Dpm20AccessReader.ReadItems"/>
    IEnumerable<Dpm20ItemRow> ReadItems();

    /// <inheritdoc cref="Dpm20AccessReader.ReadFactVariablePropertyIds"/>
    IEnumerable<int> ReadFactVariablePropertyIds();

    /// <inheritdoc cref="Dpm20AccessReader.ReadNonKeyHeaderPropertyIds"/>
    IEnumerable<int> ReadNonKeyHeaderPropertyIds();

    /// <inheritdoc cref="Dpm20AccessReader.ReadSubCategories"/>
    IEnumerable<Dpm20SubCategoryRow> ReadSubCategories();

    /// <inheritdoc cref="Dpm20AccessReader.ReadMetricSubCategoryCandidates"/>
    IEnumerable<Dpm20MetricSubCategoryCandidateRow> ReadMetricSubCategoryCandidates();

    /// <inheritdoc cref="Dpm20AccessReader.CutoffReleaseId"/>
    int CutoffReleaseId { get; }

    /// <inheritdoc cref="Dpm20AccessReader.ReadContextCompositionPropertyIds"/>
    IEnumerable<int> ReadContextCompositionPropertyIds();

    /// <inheritdoc cref="Dpm20AccessReader.ReadKeyVariablePropertyIds"/>
    IEnumerable<int> ReadKeyVariablePropertyIds();

    /// <inheritdoc cref="Dpm20AccessReader.ReadReleases"/>
    IEnumerable<Dpm20ReleaseRow> ReadReleases();
}
