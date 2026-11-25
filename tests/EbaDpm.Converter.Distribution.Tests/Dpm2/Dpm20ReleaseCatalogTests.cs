using EbaDpm.Converter.Distribution.Access.Dpm20;
using EbaDpm.Converter.Distribution.Mapping.Dpm20;

namespace EbaDpm.Converter.Distribution.Tests.Dpm2;

/// <summary>
/// <see cref="Dpm20ReleaseCatalog"/> (#73): one SQLite file per release, a release is "empty" (no
/// file) when no exportable module version starts in it. Exportable = the module is not a document
/// module; every framework counts, DORA included (#75). The pure form is exercised without Access;
/// the reader overload runs over a synthetic <c>.accdb</c> (<see cref="AceFactAttribute"/>).
/// </summary>
[Trait("Tier", "Synthetic")]
public sealed class Dpm20ReleaseCatalogTests
{
    // Frameworks are never looked at by the catalog; FrameworkId 3 stands for DORA.
    private static readonly Dpm20ModuleRow[] Modules =
    [
        new(10, 1, false), // regular
        new(11, 2, true), // document module
        new(12, 3, false), // a DORA module
    ];

    private static readonly Dpm20ModuleVersionHistoryRow[] History =
    [
        new(100, 10, 1, 1, null, null, null), // starts in 4.0
        new(104, 10, 1, 1, 2, null, null), // starts in 4.0, ended in 4.1: still published in 4.0
        new(101, 11, 2, 2, null, null, null), // starts in 4.1 but is a document module
        new(102, 10, 1, 3, null, null, null), // starts in 4.2
        new(103, 12, 3, 3, null, null, null), // starts in 4.2, DORA
    ];

    // Deliberately NOT in ReleaseID order.
    private static readonly Dpm20ReleaseRow[] ReleasesUnordered =
    [
        new(4, "4.2.1.1", null, null, null, false),
        new(2, "4.1", null, null, null, false),
        new(3, "4.2", null, null, null, false),
        new(1, "4.0", null, null, null, true),
    ];

    private static IReadOnlyList<(string Code, int Count, bool Empty)> Summaries(
        IEnumerable<Dpm20ModuleRow>? modules = null, IEnumerable<Dpm20ModuleVersionHistoryRow>? history = null)
        => Dpm20ReleaseCatalog.Build(ReleasesUnordered, modules ?? Modules, history ?? History)
            .Select(r => (r.Code, r.ExportableModuleVersions, r.IsEmpty))
            .ToList();

    [Fact]
    public void Build_ListsEveryRelease_InReleaseIdOrder_WithTheirExportableModuleVersionCounts()
    {
        var summaries = Dpm20ReleaseCatalog.Build(ReleasesUnordered, Modules, History);

        Assert.Equal([1, 2, 3, 4], summaries.Select(r => r.ReleaseId));
        Assert.Equal(
            [("4.0", 2, false), ("4.1", 0, true), ("4.2", 2, false), ("4.2.1.1", 0, true)],
            Summaries());
    }

    [Fact]
    public void Build_DocumentModuleVersions_AreNotCounted_SoAReleaseWithOnlyThemIsEmpty()
    {
        Assert.Equal(("4.1", 0, true), Summaries().Single(s => s.Code == "4.1"));

        // Positive control: the same world, with the module no longer flagged as a document module,
        // counts that module version and the release stops being empty.
        Dpm20ModuleRow[] notDocument = [new(10, 1, false), new(11, 2, false), new(12, 3, false)];
        Assert.Equal(("4.1", 1, false), Summaries(modules: notDocument).Single(s => s.Code == "4.1"));
    }

    [Fact]
    public void Build_DoraModuleVersions_AreCounted()
    {
        Assert.Equal(2, Summaries().Single(s => s.Code == "4.2").Count);

        // Positive control: without the DORA module version the count drops to 1, so the 2 above
        // really includes it.
        var withoutDora = History.Where(h => h.ModuleVId != 103).ToList();
        Assert.Equal(1, Summaries(history: withoutDora).Single(s => s.Code == "4.2").Count);

        // A release whose only module version is a DORA one is not empty.
        var onlyDora = History.Where(h => h.ModuleVId == 103).ToList();
        Assert.Equal(("4.2", 1, false), Summaries(history: onlyDora).Single(s => s.Code == "4.2"));
    }

    [Fact]
    public void Build_AModuleVersionCountsInItsStartRelease_EvenWhenItEndedLater()
    {
        // 100 and 104 both start in 4.0; 104 ends in 4.1. Both are published in 4.0.
        Assert.Equal(2, Summaries().Single(s => s.Code == "4.0").Count);
        Assert.Equal(0, Summaries().Single(s => s.Code == "4.1").Count);
    }

    [Fact]
    public void Build_WithNoModuleVersionAtAll_EveryReleaseIsEmpty()
    {
        var summaries = Summaries(history: []);

        Assert.Equal(4, summaries.Count);
        Assert.All(summaries, s => Assert.True(s.Empty));
    }

    [Fact]
    public void Build_NullInputs_AreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Dpm20ReleaseCatalog.Build(null!, Modules, History));
        Assert.Throws<ArgumentNullException>(() => Dpm20ReleaseCatalog.Build(ReleasesUnordered, null!, History));
        Assert.Throws<ArgumentNullException>(() => Dpm20ReleaseCatalog.Build(ReleasesUnordered, Modules, null!));
    }

    private static string CreateCatalogSource() => Dpm2SyntheticSource.Create(c =>
    {
        // Release ids deliberately not contiguous with the insertion order.
        Dpm2SyntheticSource.Exec(c, "INSERT INTO [Release] ([ReleaseID],[Code],[IsCurrent]) VALUES (3,'4.2',-1)");
        Dpm2SyntheticSource.Exec(c, "INSERT INTO [Release] ([ReleaseID],[Code],[IsCurrent]) VALUES (1,'4.0',0)");
        Dpm2SyntheticSource.Exec(c, "INSERT INTO [Release] ([ReleaseID],[Code],[IsCurrent]) VALUES (2,'4.1',0)");
        Dpm2SyntheticSource.Exec(c, "INSERT INTO [Module] VALUES (10,1,0)");
        Dpm2SyntheticSource.Exec(c, "INSERT INTO [Module] VALUES (11,2,-1)");
        Dpm2SyntheticSource.Exec(c, "INSERT INTO [Module] VALUES (12,3,0)");
        Dpm2SyntheticSource.Exec(c, "INSERT INTO [ModuleVersion] ([ModuleVID],[ModuleID],[Code],[Name],[StartReleaseID]) VALUES (100,10,'a','A',1)");
        Dpm2SyntheticSource.Exec(c, "INSERT INTO [ModuleVersion] ([ModuleVID],[ModuleID],[Code],[Name],[StartReleaseID]) VALUES (101,11,'d','D',2)");
        Dpm2SyntheticSource.Exec(c, "INSERT INTO [ModuleVersion] ([ModuleVID],[ModuleID],[Code],[Name],[StartReleaseID]) VALUES (102,10,'a2','A2',3)");
        Dpm2SyntheticSource.Exec(c, "INSERT INTO [ModuleVersion] ([ModuleVID],[ModuleID],[Code],[Name],[StartReleaseID]) VALUES (103,12,'x','X',3)");
    });

    [AceFact]
    public void ReaderOverload_ReadsTheHistoryOfEveryRelease_NotOnlyTheOnesUpToTheLatest()
    {
        var accdb = CreateCatalogSource();
        try
        {
            using var reader = new Dpm20AccessReader(accdb);
            reader.Open();

            var summaries = Dpm20ReleaseCatalog.Build(reader);

            Assert.Equal(
                [("4.0", 1), ("4.1", 0), ("4.2", 2)],
                summaries.Select(s => (s.Code, s.ExportableModuleVersions)));
        }
        finally
        {
            Dpm2SyntheticSource.Delete(accdb);
        }
    }

    [AceFact]
    public void ReaderOverload_WithARequestedCutoff_IsRejected()
    {
        // A reader with a requested cutoff only sees the history up to it, which would silently hide
        // later releases from the catalog.
        var accdb = CreateCatalogSource();
        try
        {
            using var reader = new Dpm20AccessReader(accdb, cutoffReleaseCode: "4.1");
            reader.Open();

            Assert.Throws<ArgumentException>(() => Dpm20ReleaseCatalog.Build(reader));
        }
        finally
        {
            Dpm2SyntheticSource.Delete(accdb);
        }
    }

    internal static string CreateCatalogSourceForCli() => CreateCatalogSource();
}
