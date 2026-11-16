using EbaDpm.Converter.Core.Access.Dpm20;

namespace EbaDpm.Converter.Core.Mapping.Dpm20;

/// <summary>
/// A release of a DPM 2.0 source with the number of exportable module versions that start in it
/// (<c>ModuleVersion.StartReleaseID = ReleaseId</c>, document modules excluded). A release with
/// zero is "empty": no SQLite file is produced for it.
/// </summary>
public sealed record Dpm20ReleaseSummary(int ReleaseId, string Code, int ExportableModuleVersions)
{
    /// <summary>True when no exportable module version starts in this release.</summary>
    public bool IsEmpty => ExportableModuleVersions == 0;
}

/// <summary>
/// Lists the releases of a DPM 2.0 source that can be exported, one SQLite file per release.
/// Exportable = the module is not a document module (<c>Module.isDocumentModule</c> false).
/// </summary>
public static class Dpm20ReleaseCatalog
{
    /// <summary>
    /// Pure form. <paramref name="moduleVersionHistory"/> must hold every module version (the
    /// history of a reader opened without a requested cutoff).
    /// </summary>
    /// <returns>Every release in <c>ReleaseID</c> order.</returns>
    public static IReadOnlyList<Dpm20ReleaseSummary> Build(
        IEnumerable<Dpm20ReleaseRow> releases,
        IEnumerable<Dpm20ModuleRow> modules,
        IEnumerable<Dpm20ModuleVersionHistoryRow> moduleVersionHistory)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(moduleVersionHistory);

        var documentModuleIds = modules.Where(m => m.IsDocumentModule).Select(m => m.ModuleId).ToHashSet();
        var countByRelease = moduleVersionHistory
            .Where(v => !documentModuleIds.Contains(v.ModuleId))
            .GroupBy(v => v.StartReleaseId)
            .ToDictionary(g => g.Key, g => g.Count());

        return releases
            .OrderBy(r => r.ReleaseId)
            .Select(r => new Dpm20ReleaseSummary(r.ReleaseId, r.Code, countByRelease.GetValueOrDefault(r.ReleaseId)))
            .ToList();
    }

    /// <summary>
    /// Thin overload over an opened reader. The reader must have been created WITHOUT a requested
    /// cutoff, so that its history covers every release.
    /// </summary>
    public static IReadOnlyList<Dpm20ReleaseSummary> Build(Dpm20AccessReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.CutoffReleaseRequested)
        {
            throw new ArgumentException(
                "The reader must be opened without a requested cutoff release.", nameof(reader));
        }

        return Build(reader.ReadReleases(), reader.ReadModules(), reader.ReadModuleVersionHistory());
    }
}
