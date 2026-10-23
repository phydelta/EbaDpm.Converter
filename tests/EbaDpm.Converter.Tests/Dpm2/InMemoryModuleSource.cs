using EbaDpm.Converter.Core.Access.Dpm20;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// In-memory <see cref="IDpm20ModuleSource"/>: returns the rows it was given, empty by default.
/// No ACE, no data files.
/// </summary>
internal sealed class InMemoryModuleSource : IDpm20ModuleSource
{
    public string CutoffReleaseCode { get; set; } = string.Empty;
    public List<Dpm20TableGroupRow> TableGroups { get; } = [];

    public IEnumerable<Dpm20TableGroupRow> ReadTableGroups() => TableGroups;
}
