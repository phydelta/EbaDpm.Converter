using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Tests.Schema;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Collection fixture (xUnit <see cref="ICollectionFixture{TFixture}"/>): runs ONCE, for the whole
/// "Dpm2" collection, the full read of the DPM 2.0 Access database
/// (<c>Data/DPM2 Database_v 4_2_20251125.accdb</c>, 755 MB) - the expensive part.
///
/// It leaves two readers open:
/// - <see cref="Reader"/>: DEFAULT cutoff release (the highest, 5 = "4.2"). Used by
///   <c>Dpm20AccessReaderTests</c> and <c>Dpm20TaxonomyDeriverTests</c>.
/// - <see cref="ReaderCutoff3"/>: EXPLICIT cutoff release (3 = "4.0"), opened separately, for the
///   test that checks that the figures change with another release.
/// When the data directory is not available the constructor does nothing (the tests are skipped).
/// </summary>
public sealed class Dpm20AccessReaderFixture : IDisposable
{
    public Dpm20AccessReader Reader { get; } = null!;
    public Dpm20AccessReader ReaderCutoff3 { get; } = null!;

    public List<Dpm20ReleaseRow> Releases { get; } = null!;
    public List<Dpm20FrameworkRow> Frameworks { get; } = null!;
    public List<Dpm20OrganisationRow> Organisations { get; } = null!;
    public List<Dpm20ModuleRow> Modules { get; } = null!;
    public List<Dpm20ModuleVersionRow> ModuleVersions { get; } = null!;
    public List<Dpm20ModuleVersionCompositionRow> ModuleVersionCompositions { get; } = null!;
    public List<Dpm20TableVersionRow> TableVersions { get; } = null!;

    /// <summary>Same kind of read as <see cref="TableVersions"/>, but with the cutoff at release 3 ("4.0").</summary>
    public List<Dpm20TableVersionRow> TableVersionsCutoff3 { get; } = null!;

    public Dpm20AccessReaderFixture()
    {
        if (!RepoPaths.IsDataAvailable)
        {
            return;
        }

        RepoPaths.EnsureAccessDpm20DatabaseExists();

        Reader = new Dpm20AccessReader(RepoPaths.AccessDpm20DatabasePath);
        Reader.Open();

        Releases = Reader.ReadReleases().ToList();
        Frameworks = Reader.ReadFrameworks().ToList();
        Organisations = Reader.ReadOrganisations().ToList();
        Modules = Reader.ReadModules().ToList();
        ModuleVersions = Reader.ReadModuleVersions().ToList();
        ModuleVersionCompositions = Reader.ReadModuleVersionCompositions().ToList();
        TableVersions = Reader.ReadTableVersions().ToList();

        ReaderCutoff3 = new Dpm20AccessReader(RepoPaths.AccessDpm20DatabasePath, cutoffReleaseId: 3);
        ReaderCutoff3.Open();
        TableVersionsCutoff3 = ReaderCutoff3.ReadTableVersions().ToList();
    }

    public void Dispose()
    {
        Reader?.Dispose();
        ReaderCutoff3?.Dispose();
    }
}

/// <summary>
/// xUnit collection definition: groups all the tests over the DPM 2.0 read so that they share a
/// single run of <see cref="Dpm20AccessReaderFixture"/> (the source Access database is 755 MB).
/// </summary>
[CollectionDefinition("Dpm2")]
public sealed class Dpm2Collection : ICollectionFixture<Dpm20AccessReaderFixture>
{
}
