using System.Globalization;
using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Distribution.Tests.Infrastructure;
using EbaDpm.Converter.Distribution.Tests.Schema;
using EbaDpm.Converter.Distribution.Tests.Support;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Distribution.Tests.Dpm2;

/// <summary>
/// Directory mode of #73 (<c>--output &lt;directory&gt;</c> without <c>--release</c>): one file per
/// non-empty release of the 4.2.1 database, each one the database as of its release R. Every
/// expectation is derived from the Access database (release list, module versions that start in R,
/// taxonomies of R); nothing is a constant copied from the output.
/// </summary>
[Collection("Dpm2PerReleaseDirectory")]
[Trait("Tier", "RealData")]
public sealed class CliPerReleaseDirectoryTests(Dpm20PerReleaseDirectoryFixture fixture)
{
    private static string Accdb => RepoPaths.AccessDpm20DatabasePath;

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static List<string> Strings(string path, string sql)
    {
        using var connection = OpenReadOnly(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
        }

        return list;
    }

    private static List<(string Code, int Id, long Exportable)> Releases() => Dpm20PerReleaseDirectoryFixture.ReleasesFromAccess();

    [DataFact]
    public void DirectoryMode_ExitsOk_AndWritesOneFilePerNonEmptyRelease_NamedAfterTheReleaseCode()
    {
        Assert.True(fixture.ExitCode == 0, $"exit={fixture.ExitCode}; stderr: {fixture.Stderr}");

        var releases = Releases();
        var nonEmpty = releases.Where(r => r.Exportable > 0).Select(r => $"dpm-{r.Code}.db").OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(nonEmpty.Count >= 2, "The source has fewer than two non-empty releases: the per-release layout would not be exercised.");

        var written = Directory.GetFiles(fixture.OutputDirectory).Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(nonEmpty, written);

        // Every release is announced in ReleaseID order, in the log, with the file it goes to.
        var previous = -1;
        foreach (var (code, _, exportable) in releases.Where(r => r.Exportable > 0))
        {
            var header = $"=== Release {code} ({exportable} exportable module versions) ->";
            var index = fixture.Stdout.IndexOf(header, StringComparison.Ordinal);
            Assert.True(index > previous, $"'{header}' is missing or out of release order in the log.");
            previous = index;
        }
    }

    [DataFact]
    public void DirectoryMode_NamesEachEmptyRelease_AsSkipped_AndWritesNoFileForIt()
    {
        var empty = Releases().Where(r => r.Exportable == 0).ToList();

        // This database may have no empty release, in which case the loop below is vacuous: the
        // skipped path itself (message, no file) is proven on a synthetic source by
        // CliReleaseTests.Dpm20Source_DirectoryMode_NamesTheFilesByRelease_SkipsTheEmptyOne_...
        // The second loop is not vacuous: no non-empty release is announced as skipped.
        foreach (var (code, _, _) in empty)
        {
            Assert.Contains($"Release {code}: no exportable module version starts in it; skipped.", fixture.Stdout, StringComparison.Ordinal);
            Assert.False(File.Exists(fixture.FileOf(code)), $"A file was written for the empty release {code}.");
        }

        foreach (var (code, _, _) in Releases().Where(r => r.Exportable > 0))
        {
            Assert.DoesNotContain($"Release {code}: no exportable module version starts in it", fixture.Stdout, StringComparison.Ordinal);
        }
    }

    [DataFact]
    public void EachFile_HoldsTheReleasesUpToItsOwn_AndOnlyItsOwnIsCurrent()
    {
        foreach (var (code, id, _) in Releases().Where(r => r.Exportable > 0))
        {
            var expected = AccessCensus.IntByCode(Accdb, $"SELECT [Code], [ReleaseID] FROM [Release] WHERE [ReleaseID] <= {id}")
                .OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
            var actual = Strings(fixture.FileOf(code), "SELECT ReleaseCode FROM mRelease ORDER BY ReleaseID");
            Assert.True(expected.SequenceEqual(actual), $"dpm-{code}.db mRelease: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}].");

            var current = Strings(fixture.FileOf(code), "SELECT ReleaseCode FROM mRelease WHERE IsCurrent = 1");
            Assert.Equal([code], current);
        }
    }

    [DataFact]
    public void EachFile_HoldsExactlyTheTaxonomiesWhoseModuleVersionsStartInItsRelease()
    {
        foreach (var (code, _, _) in Releases().Where(r => r.Exportable > 0))
        {
            var expected = AccessCensus.OfRelease(AccessCensus.EmittedModuleCodesByTaxonomy(Accdb, code), code).Keys
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.NotEmpty(expected);

            var actual = Strings(fixture.FileOf(code), "SELECT TaxonomyCode FROM mTaxonomy ORDER BY TaxonomyCode")
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.True(expected.SequenceEqual(actual), $"dpm-{code}.db mTaxonomy: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}].");
            Assert.All(actual, t => Assert.EndsWith(" " + code, t, StringComparison.Ordinal));
        }
    }

    [DataFact]
    public void EachFile_HoldsExactlyTheNonDocumentModuleVersionsStartingInItsRelease_AndTheirSchemaRefCarriesIt()
    {
        var documentVersionsExercised = 0L;
        foreach (var (code, id, _) in Releases().Where(r => r.Exportable > 0))
        {
            var expected = AccessCensus.IntByCode(
                Accdb,
                "SELECT mv.[Code], mv.[ModuleVID] FROM [ModuleVersion] mv INNER JOIN [Module] m ON m.[ModuleID] = mv.[ModuleID] "
                + $"WHERE mv.[StartReleaseID] = {id} AND m.[isDocumentModule] = 0 AND (mv.[EndReleaseID] IS NULL OR mv.[EndReleaseID] > {id})")
                .Keys.Select(c => c.ToUpperInvariant()).OrderBy(c => c, StringComparer.Ordinal).ToList();
            var actual = Strings(fixture.FileOf(code), "SELECT ModuleCode FROM mModule ORDER BY ModuleCode")
                .OrderBy(c => c, StringComparer.Ordinal).ToList();
            Assert.True(expected.SequenceEqual(actual), $"dpm-{code}.db mModule: only in Access [{string.Join(", ", expected.Except(actual))}], only in the file [{string.Join(", ", actual.Except(expected))}].");

            // A module that did not change in a later release stays in the file of the release that
            // published it: its XBRLSchemaRef carries THAT release.
            var refs = Strings(fixture.FileOf(code), "SELECT XBRLSchemaRef FROM mModule");
            Assert.All(refs, r => Assert.Contains($"/{code}/mod/", r, StringComparison.Ordinal));

            // The document modules that start in this release are in the source and absent from the file.
            var documents = AccessCensus.IntByCode(
                Accdb,
                "SELECT mv.[Code], mv.[ModuleVID] FROM [ModuleVersion] mv INNER JOIN [Module] m ON m.[ModuleID] = mv.[ModuleID] "
                + $"WHERE mv.[StartReleaseID] = {id} AND m.[isDocumentModule] <> 0")
                .Keys.Select(c => c.ToUpperInvariant()).ToList();
            documentVersionsExercised += documents.Count;
            Assert.All(documents, d => Assert.DoesNotContain(d, actual));
        }

        // Positive control: some release of this database starts document module versions, so the
        // isDocumentModule filter was exercised by at least one file rather than vacuously.
        Assert.True(documentVersionsExercised > 0, "No release of the source starts a document module version: the filter was not exercised.");
    }

    [DataFact]
    public void TheDictionaryOfConsecutiveFiles_OnlyGrows_ByBusinessKey()
    {
        // As of R, with no end condition: a later release holds every domain member, dimension and
        // hierarchy of an earlier one. Compared by business key (codes), never by surrogate ID.
        var files = Releases().Where(r => r.Exportable > 0).Select(r => r.Code).ToList();
        const string members = "SELECT d.DomainCode || '|' || m.MemberXBRLCode FROM mMember m JOIN mDomain d ON d.DomainID = m.DomainID";
        const string dimensions = "SELECT DimensionXBRLCode FROM mDimension";
        const string domains = "SELECT DomainCode FROM mDomain";
        const string hierarchies = "SELECT d.DomainCode || '|' || h.HierarchyCode FROM mHierarchy h JOIN mDomain d ON d.DomainID = h.DomainID";

        var strictlyGrew = false;
        for (var i = 0; i + 1 < files.Count; i++)
        {
            foreach (var (what, sql) in new[] { ("members", members), ("dimensions", dimensions), ("domains", domains), ("hierarchies", hierarchies) })
            {
                var earlier = Strings(fixture.FileOf(files[i]), sql).ToHashSet(StringComparer.Ordinal);
                var later = Strings(fixture.FileOf(files[i + 1]), sql).ToHashSet(StringComparer.Ordinal);
                var missing = earlier.Except(later).Take(5).ToList();
                Assert.True(
                    missing.Count == 0,
                    $"{what} of dpm-{files[i]}.db absent from dpm-{files[i + 1]}.db (first {missing.Count}): [{string.Join(", ", missing)}].");
                strictlyGrew |= later.Count > earlier.Count;
            }
        }

        // Positive control: the dictionary does change between releases of this database, so the
        // inclusion above was not an equality of identical sets.
        Assert.True(strictlyGrew, "No dictionary table grew between consecutive files: the inclusion check was not exercised.");
    }

    [DataFact]
    public void EveryFile_PassesPlaneA_WithNoCriticalFailure()
    {
        // Every file is validated and every problem is collected, so one run shows the whole evidence.
        var problems = new List<string>();
        foreach (var (code, _, _) in Releases().Where(r => r.Exportable > 0))
        {
            var result = ValidatorRunCache.Run(fixture.FileOf(code), referencePath: null);
            var critical = result.Report.Checks.Where(c => c.Layer == "critical").ToList();
            if (critical.Count == 0)
            {
                problems.Add($"dpm-{code}.db: 0 critical checks in the report - the harness did not run.");
            }

            problems.AddRange(critical.Where(c => c.Status == "fail")
                .Select(c => $"dpm-{code}.db {c.Id}: {c.Failed}/{c.Examined} -- {c.Statement}"));
            if (result.Report.Summary.StaleExceptions != 0)
            {
                problems.Add($"dpm-{code}.db: {result.Report.Summary.StaleExceptions} stale known exception(s).");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [DataFact]
    public void RunningAgainWithoutOverwrite_IsAnArgumentError_ListsEveryTarget_AndLeavesTheFilesUntouched()
    {
        var files = Directory.GetFiles(fixture.OutputDirectory);
        Assert.NotEmpty(files);
        var stamps = files.ToDictionary(f => f, f => (File.GetLastWriteTimeUtc(f), new System.IO.FileInfo(f).Length));

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliRunner.Run(["--source", Accdb, "--output", fixture.OutputDirectory], stdout, stderr);

        Assert.Equal(CliRunner.ExitArgumentError, exit);
        foreach (var file in files)
        {
            Assert.Contains(file, stderr.ToString(), StringComparison.Ordinal);
            Assert.Equal(stamps[file], (File.GetLastWriteTimeUtc(file), new System.IO.FileInfo(file).Length));
        }

        Assert.Equal(files.Length, Directory.GetFiles(fixture.OutputDirectory).Length);
    }
}
