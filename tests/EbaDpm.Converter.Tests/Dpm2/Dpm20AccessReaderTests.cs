using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// <c>Dpm20AccessReader</c> - the release cutoff. Every expected figure is DERIVED here from the
/// same <c>.accdb</c> with an independent OLEDB query (<see cref="AccessCensus"/>, no production
/// code): the cutoff is release "4.2" (<see cref="RepoPaths.Cutoff42ReleaseCode"/>), looked up by
/// its <c>Release.Code</c>, and the rows "in force" are those with
/// <c>StartReleaseID &lt;= R AND (EndReleaseID IS NULL OR EndReleaseID &gt; R)</c>.
/// </summary>
[Collection("Dpm2")]
public sealed class Dpm20AccessReaderTests(Dpm20AccessReaderFixture fixture)
{
    private static string Accdb => RepoPaths.AccessDpm20DatabasePath;

    [DataFact]
    public void ReadReleases_MatchesTheAccessReleaseTable()
    {
        var expectedCount = AccessCensus.Count(Accdb, "Release");
        Assert.True(
            expectedCount == fixture.Releases.Count,
            $"[Release] has {expectedCount} rows in the source, the reader returned {fixture.Releases.Count}.");

        // By business key: every Release.Code maps to the same ReleaseID in the source and in the read.
        var expected = AccessCensus.IntByCode(Accdb, "SELECT [Code], [ReleaseID] FROM [Release]");
        var actual = fixture.Releases.ToDictionary(r => r.Code, r => r.ReleaseId, StringComparer.Ordinal);
        Assert.Equal(expected.OrderBy(kv => kv.Key, StringComparer.Ordinal), actual.OrderBy(kv => kv.Key, StringComparer.Ordinal));
    }

    [DataFact]
    public void ReadFrameworks_MatchesTheAccessFrameworkTable()
        => AssertCount("Framework", AccessCensus.Count(Accdb, "Framework"), fixture.Frameworks.Count);

    [DataFact]
    public void ReadOrganisations_MatchesTheAccessOrganisationTable()
        => AssertCount("Organisation", AccessCensus.Count(Accdb, "Organisation"), fixture.Organisations.Count);

    [DataFact]
    public void ReadModules_MatchesTheAccessModuleTable()
        => AssertCount("Module", AccessCensus.Count(Accdb, "Module"), fixture.Modules.Count);

    [DataFact]
    public void ReadModuleVersions_MatchesTheRowsInForceAtTheCutoff()
        => AssertCount(
            $"ModuleVersion in force at {RepoPaths.Cutoff42ReleaseCode}",
            AccessCensus.CountInForce(Accdb, "ModuleVersion", RepoPaths.Cutoff42ReleaseCode),
            fixture.ModuleVersions.Count);

    // RAW-TABLE count: ModuleVersionComposition has no release columns and the reader returns the
    // whole table by design (pruning happens through the in-force module versions).
    [DataFact]
    public void ReadModuleVersionCompositions_MatchesTheRawAccessTable()
        => AssertCount("ModuleVersionComposition (raw table)", AccessCensus.Count(Accdb, "ModuleVersionComposition"), fixture.ModuleVersionCompositions.Count);

    [DataFact]
    public void ReadTableVersions_MatchesTheRowsInForceAtTheCutoff()
        => AssertCount(
            $"TableVersion in force at {RepoPaths.Cutoff42ReleaseCode}",
            AccessCensus.CountInForce(Accdb, "TableVersion", RepoPaths.Cutoff42ReleaseCode),
            fixture.TableVersions.Count);

    /// <summary>
    /// The abstract tables are NOT filtered out when reading. If someone filtered them here, the
    /// mapping would lose its <c>eba_tg...</c> nodes of <c>mTemplateOrTable</c>, and <c>mTable</c>
    /// would still add up with NO SYMPTOM. The expected count is derived from <c>Table.IsAbstract</c>.
    /// </summary>
    [DataFact]
    public void ReadTableVersions_IncludesTheAbstracts_SoMappingCanBuildTheTemplateTreeLater()
    {
        var expectedAbstract = AccessCensus.CountAbstractTableVersionsInForce(Accdb, RepoPaths.Cutoff42ReleaseCode);
        var abstractCount = fixture.TableVersions.Count(tv => tv.IsAbstract);

        AssertCount($"abstract TableVersion in force at {RepoPaths.Cutoff42ReleaseCode}", expectedAbstract, abstractCount);
        // The abstracts are INSIDE the current versions, not on top of them.
        Assert.True(
            abstractCount < fixture.TableVersions.Count,
            $"{abstractCount} abstract table versions out of {fixture.TableVersions.Count} current ones: the abstracts must be a strict subset.");
    }

    /// <summary>
    /// The invariant that proves the exclusive <c>&gt;</c> of the cutoff: two versions of the SAME
    /// entity cannot be current in the same release. What the READER returns (not a query of our
    /// own) is grouped by <c>ModuleID</c> and by <c>TableID</c>, and ZERO groups with more than one
    /// version are required. With the inclusive criterion (<c>&gt;=</c>) that count shoots up: this
    /// test is exactly what would detect that regression. It is an INVARIANT test, not a count
    /// test: it looks at no reference.
    /// </summary>
    [DataFact]
    public void CurrentVersions_NeverHaveTwoRowsForTheSameEntity_ModuleVersion()
    {
        var groupsWithMoreThanOneVersion = fixture.ModuleVersions
            .GroupBy(mv => mv.ModuleId)
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.Empty(groupsWithMoreThanOneVersion);
    }

    /// <summary>See <see cref="CurrentVersions_NeverHaveTwoRowsForTheSameEntity_ModuleVersion"/>, same invariant over <c>TableVersion</c>.</summary>
    [DataFact]
    public void CurrentVersions_NeverHaveTwoRowsForTheSameEntity_TableVersion()
    {
        var groupsWithMoreThanOneVersion = fixture.TableVersions
            .GroupBy(tv => tv.TableId)
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.Empty(groupsWithMoreThanOneVersion);
    }

    [DataFact]
    public void ExplicitCutoff_ResolvesToTheReleaseOfThatCode()
    {
        Assert.Equal(AccessCensus.ReleaseId(Accdb, RepoPaths.Cutoff42ReleaseCode), fixture.Reader.CutoffReleaseId);
        Assert.Equal(RepoPaths.Cutoff42ReleaseCode, fixture.Reader.CutoffReleaseCode);
    }

    /// <summary>
    /// With another explicit cutoff release (the one coded "4.0") the current-version figures
    /// CHANGE: the reader returns the rows in force at THAT release (derived independently) and the
    /// count is DIFFERENT from the one at the default cutoff - proof that the cutoff really filters
    /// by the release passed and does not ignore the parameter.
    /// </summary>
    [DataFact]
    public void ExplicitCutoffRelease_ChangesTheCurrentCounts()
    {
        const string otherCode = "4.0";
        Assert.Equal(AccessCensus.ReleaseId(Accdb, otherCode), fixture.ReaderCutoff3.CutoffReleaseId);
        Assert.Equal(otherCode, fixture.ReaderCutoff3.CutoffReleaseCode);

        AssertCount(
            $"TableVersion in force at {otherCode}",
            AccessCensus.CountInForce(Accdb, "TableVersion", otherCode),
            fixture.TableVersionsCutoff3.Count);

        Assert.True(
            fixture.TableVersions.Count != fixture.TableVersionsCutoff3.Count,
            $"TableVersion in force: {fixture.TableVersions.Count} at {RepoPaths.Cutoff42ReleaseCode} and {fixture.TableVersionsCutoff3.Count} at {otherCode}; the cutoff parameter would be ignored.");
    }

    private static void AssertCount(string what, long expected, long actual)
        => Assert.True(expected == actual, $"{what}: the source Access query counts {expected}, the reader returned {actual}.");
}
