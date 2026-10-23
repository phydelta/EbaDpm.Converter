using EbaDpm.Converter.Core.Access.Dpm20;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// In-memory <see cref="IDpm20StructureSource"/>: returns the rows it was given, empty by default.
/// No ACE, no data files.
/// </summary>
internal sealed class InMemoryStructureSource : IDpm20StructureSource
{
    public string CutoffReleaseCode { get; set; } = string.Empty;
    public List<Dpm20TableGroupRow> TableGroups { get; } = [];
    public List<Dpm20TableGroupCompositionRow> TableGroupCompositions { get; } = [];

    public IEnumerable<Dpm20TableGroupRow> ReadTableGroups() => TableGroups;

    public IEnumerable<Dpm20TableGroupCompositionRow> ReadTableGroupCompositions() => TableGroupCompositions;
}
