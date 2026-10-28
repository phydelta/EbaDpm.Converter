using EbaDpm.Converter.Core.Access.Dpm20;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// In-memory <see cref="IDpm20DictionarySource"/>: returns the rows it was given, empty by default.
/// No ACE, no data files.
/// </summary>
internal sealed class InMemoryDictionarySource : IDpm20DictionarySource
{
    public List<Dpm20DataTypeRow> DataTypes { get; } = [];
    public List<Dpm20PropertyRow> Properties { get; } = [];
    public List<Dpm20PropertyCategoryRow> PropertyCategories { get; } = [];
    public List<Dpm20CategoryRow> Categories { get; } = [];
    public List<Dpm20SuperCategoryCompositionRow> SuperCategoryCompositions { get; } = [];
    public List<Dpm20ItemCategoryRow> ItemCategories { get; } = [];
    public List<Dpm20ItemRow> Items { get; } = [];
    public List<int> FactVariablePropertyIds { get; } = [];
    public List<int> NonKeyHeaderPropertyIds { get; } = [];
    public List<Dpm20SubCategoryRow> SubCategories { get; } = [];
    public List<Dpm20MetricSubCategoryCandidateRow> MetricSubCategoryCandidates { get; } = [];

    /// <summary>Cutoff release of the source; 0 keeps every PropertyCategory row with StartReleaseId &lt;= 0 out, so tests that need one set it.</summary>
    public int CutoffReleaseId { get; set; }
    public List<int> ContextCompositionPropertyIds { get; } = [];
    public List<int> KeyVariablePropertyIds { get; } = [];
    public List<Dpm20ReleaseRow> Releases { get; } = [];

    public IEnumerable<Dpm20DataTypeRow> ReadDataTypes() => DataTypes;

    public IEnumerable<Dpm20PropertyRow> ReadProperties() => Properties;

    public IEnumerable<Dpm20PropertyCategoryRow> ReadPropertyCategories() => PropertyCategories;

    public IEnumerable<Dpm20CategoryRow> ReadCategories() => Categories;

    public IEnumerable<Dpm20SuperCategoryCompositionRow> ReadSuperCategoryCompositions() => SuperCategoryCompositions;

    public IEnumerable<Dpm20ItemCategoryRow> ReadItemCategories() => ItemCategories;

    public IEnumerable<Dpm20ItemRow> ReadItems() => Items;

    public IEnumerable<int> ReadFactVariablePropertyIds() => FactVariablePropertyIds;

    public IEnumerable<int> ReadNonKeyHeaderPropertyIds() => NonKeyHeaderPropertyIds;

    public IEnumerable<Dpm20SubCategoryRow> ReadSubCategories() => SubCategories;

    public IEnumerable<Dpm20MetricSubCategoryCandidateRow> ReadMetricSubCategoryCandidates() => MetricSubCategoryCandidates;

    public IEnumerable<int> ReadContextCompositionPropertyIds() => ContextCompositionPropertyIds;

    public IEnumerable<int> ReadKeyVariablePropertyIds() => KeyVariablePropertyIds;

    public IEnumerable<Dpm20ReleaseRow> ReadReleases() => Releases;
}
