namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// <c>Dpm20AccessReader</c> - the release cutoff. Figures measured against the source, all with
/// cutoff release 5 ("4.2").
/// </summary>
[Collection("Dpm2")]
public sealed class Dpm20AccessReaderTests(Dpm20AccessReaderFixture fixture)
{
    [DataFact]
    public void ReadReleases_Returns5RowsWithTheExpectedCodesAndIds()
    {
        Assert.Equal(5, fixture.Releases.Count);

        var byCode = fixture.Releases.ToDictionary(r => r.Code, r => r.ReleaseId);
        Assert.Equal(1, byCode["3.4"]);
        Assert.Equal(2, byCode["3.5"]);
        Assert.Equal(3, byCode["4.0"]);
        Assert.Equal(4, byCode["4.1"]);
        Assert.Equal(5, byCode["4.2"]);
    }

    [DataFact]
    public void ReadFrameworks_Returns20Rows() => Assert.Equal(20, fixture.Frameworks.Count);

    [DataFact]
    public void ReadOrganisations_Returns3Rows() => Assert.Equal(3, fixture.Organisations.Count);

    [DataFact]
    public void ReadModules_Returns55Rows() => Assert.Equal(55, fixture.Modules.Count);

    [DataFact]
    public void ReadModuleVersions_Returns53CurrentRows() => Assert.Equal(53, fixture.ModuleVersions.Count);

    [DataFact]
    public void ReadModuleVersionCompositions_Returns2956Rows()
        => Assert.Equal(2956, fixture.ModuleVersionCompositions.Count);

    [DataFact]
    public void ReadTableVersions_Returns929CurrentRows() => Assert.Equal(929, fixture.TableVersions.Count);

    /// <summary>
    /// The 126 abstract tables are NOT filtered out when reading. If someone filtered them here,
    /// the mapping would lose its 126 <c>eba_tg...</c> nodes of <c>mTemplateOrTable</c>, and
    /// <c>mTable</c> would still add up to 788 with NO SYMPTOM. This test prevents that silent
    /// failure.
    /// </summary>
    [DataFact]
    public void ReadTableVersions_IncludesTheAbstracts_SoMappingCanBuildTheTemplateTreeLater()
    {
        var abstractCount = fixture.TableVersions.Count(tv => tv.IsAbstract);

        Assert.Equal(126, abstractCount);
        // The abstracts are INSIDE the 929, not on top of them.
        Assert.True(abstractCount < fixture.TableVersions.Count);
    }

    /// <summary>
    /// The invariant that proves the exclusive <c>&gt;</c> of the cutoff: two versions of the SAME
    /// entity cannot be current in the same release. What the READER returns (not a query of our
    /// own) is grouped by <c>ModuleID</c> and by <c>TableID</c>, and ZERO groups with more than one
    /// version are required. With the inclusive criterion (<c>&gt;=</c>) that count shoots up to
    /// 174,955 collisions across the six versioned tables of the source: this test is exactly what
    /// would detect that regression. It is an INVARIANT test, not a count test: it looks at no
    /// reference.
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
    public void DefaultCutoffRelease_IsTheHighestOne_5_42()
    {
        Assert.Equal(5, fixture.Reader.CutoffReleaseId);
        Assert.Equal("4.2", fixture.Reader.CutoffReleaseCode);
    }

    /// <summary>
    /// With another explicit cutoff release (3 = "4.0") the current-version figures CHANGE: no
    /// concrete expected value is given, only that it is DIFFERENT from the 929 current in 4.2 -
    /// proof that the cutoff really filters by the release passed and does not ignore the
    /// parameter.
    /// </summary>
    [DataFact]
    public void ExplicitCutoffRelease_ChangesTheCurrentCounts()
    {
        Assert.Equal(3, fixture.ReaderCutoff3.CutoffReleaseId);
        Assert.Equal("4.0", fixture.ReaderCutoff3.CutoffReleaseCode);

        Assert.NotEqual(929, fixture.TableVersionsCutoff3.Count);
    }
}
