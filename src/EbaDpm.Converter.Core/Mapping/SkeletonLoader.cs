using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Mapping;

/// <summary>
/// Loads the dictionary "skeleton": <c>mOwner</c>, <c>mOwnerParent</c>, <c>mLanguage</c>,
/// <c>mRelease</c>, <c>mReportingFramework</c>, <c>mTaxonomy</c> and <c>mConcept</c>. It honours
/// the mandatory taxonomy filter in everything that depends on the taxonomy, including
/// <c>mReportingFramework</c>: a framework without any taxonomy would be a dangling object, and
/// all four references filter it without exception.
/// <c>mOwner</c>/<c>mOwnerParent</c>/<c>mLanguage</c> are constants unaffected by the filter.
/// <c>mRelease</c> extends beyond the selected taxonomies, down to the dynamic floor: not because
/// <c>mConcept.ReleaseID</c> always needs to be resolved (it stays NULL except in three cases),
/// but because the releases named by the suffix of a <c>Dimension</c>/<c>Member</c> (see
/// <c>DictionaryLoader</c>) can be older than those of the selected taxonomies, and
/// <c>mRelease</c> must still be able to resolve them.
///
/// Out of scope here (do not touch): <c>mDomain</c>, <c>mMember</c>, <c>mDimension</c>,
/// <c>mMetric</c>, <c>mHierarchy</c>, <c>mHierarchyNode</c>.
///
/// <c>mConceptTranslation</c> IS seeded here, for <c>ReportingFramework</c>
/// (<c>FrameworkLabel</c>) and <c>Taxonomy</c> (<c>TaxonomyLabel</c>); see the end of
/// <see cref="Load"/>. The rest of the dictionary is seeded by <see cref="DictionaryLoader"/>.
/// </summary>
public static class SkeletonLoader
{
    /// <summary>Row count written per table, for the CLI report.</summary>
    public sealed record Result(
        int OwnerRows,
        int OwnerParentRows,
        int LanguageRows,
        int ReportingFrameworkRows,
        int TaxonomyRows,
        int ReleaseRows,
        int ConceptRows,
        string FloorReleaseCode);

    // ------------------------------------------------------------------
    // mOwner
    // ------------------------------------------------------------------

    /// <summary>
    /// The two <c>mOwner</c> rows that do not come from Access: fixed institutional identities of
    /// the DPM distribution format, not business information. Row 1 (EBA) is always built from
    /// Access's <c>Owner</c>.
    ///
    /// Documented assumptions:
    /// - Access has no "OwnerCode" column separate from "OwnerPrefix" (the reference does have both
    ///   columns, but with the same literal value in all three rows, as checked against the
    ///   reference database). <c>OwnerCode := OwnerPrefix</c> is derived also for row 1 (Access).
    /// - <c>OwnerLocation</c> for the two constant rows is set equal to <c>OwnerNamespace</c>: in
    ///   the reference it differs (e.g. Eurofiling has
    ///   "http://www.eurofiling.info/eu/fr/xbrl/" in Location versus
    ///   "http://www.eurofiling.info/xbrl/" in Namespace), but that specific value is derived from
    ///   the XBRL package, which we do not have: it cannot be reproduced without inventing data.
    /// </summary>
    private static readonly IReadOnlyList<(int OwnerId, string OwnerName, string OwnerCode, string OwnerNamespace, string OwnerPrefix, string? OwnerCopyright)> ConstantOwners =
    [
        (2, "Technical", "Technical", "http://technical.info", "Technical", null),
        (3, "Eurofiling", "eu", "http://www.eurofiling.info/xbrl/", "eu", null),
    ];

    private static void LoadOwnerLanguageAndParent(IDpmSourceReader source, SqliteConnection destination)
    {
        var accessOwner = source.ReadOwners().SingleOrDefault()
            ?? throw new InvalidOperationException(
                "The Access Owner table is empty; exactly 1 row was expected.");

        using (var owners = new SqliteBatchWriter(
            destination,
            "mOwner",
            ["OwnerID", "OwnerName", "OwnerCode", "OwnerNamespace", "OwnerLocation", "OwnerPrefix", "OwnerCopyright", "ParentOwnerID", "ConceptID"]))
        {
            owners.AddRow(
                accessOwner.OwnerId,
                accessOwner.OwnerName,
                accessOwner.OwnerPrefix, // OwnerCode derived from OwnerPrefix (assumption, see doc above)
                accessOwner.OwnerNamespace,
                accessOwner.OwnerLocation,
                accessOwner.OwnerPrefix,
                accessOwner.OwnerCopyright,
                accessOwner.ParentOwnerId,
                accessOwner.ConceptId);

            foreach (var constant in ConstantOwners)
            {
                owners.AddRow(
                    constant.OwnerId,
                    constant.OwnerName,
                    constant.OwnerCode,
                    constant.OwnerNamespace,
                    constant.OwnerNamespace, // OwnerLocation := OwnerNamespace (assumption, see doc above)
                    constant.OwnerPrefix,
                    constant.OwnerCopyright,
                    null,
                    null);
            }
        }

        using (var ownerParent = new SqliteBatchWriter(destination, "mOwnerParent", ["OwnerID", "ParentOwnerID"]))
        {
            // Constant, 1 row: EBA (1) is a child of Eurofiling (3).
            ownerParent.AddRow(1, 3);
        }

        using (var language = new SqliteBatchWriter(destination, "mLanguage", ["LanguageID", "LanguageName", "EnglishName", "IsoCode", "ConceptID"]))
        {
            // Constant, 1 row: English only.
            language.AddRow(1, null, null, "en", null);
        }
    }

    // ------------------------------------------------------------------
    // mReportingFramework: filtered by the taxonomy selection
    // ------------------------------------------------------------------

    /// <summary>
    /// Only the frameworks of the selected taxonomies. Across the four references, none has a
    /// framework without at least one of its taxonomies selected (a dangling framework would be
    /// incoherent), so the framework is NOT a common part: it is filtered like the explicit part.
    /// </summary>
    private static List<AccessReportingFrameworkRow> LoadReportingFrameworks(
        IDpmSourceReader source, SqliteConnection destination, IReadOnlyList<AccessTaxonomyRow> selectedTaxonomies)
    {
        var selectedFrameworkIds = selectedTaxonomies
            .Where(t => t.FrameworkId.HasValue)
            .Select(t => t.FrameworkId!.Value)
            .ToHashSet();

        var frameworks = source.ReadReportingFrameworks()
            .Where(f => selectedFrameworkIds.Contains(f.FrameworkId))
            .ToList();

        using var writer = new SqliteBatchWriter(
            destination, "mReportingFramework", ["FrameworkID", "FrameworkCode", "FrameworkLabel", "ConceptID"]);

        foreach (var framework in frameworks)
        {
            writer.AddRow(framework.FrameworkId, framework.FrameworkCode, framework.FrameworkLabel, framework.ConceptId);
        }

        return frameworks;
    }

    // ------------------------------------------------------------------
    // mTaxonomy: filtered by the taxonomy selection, lower-case code
    // ------------------------------------------------------------------

    /// <summary>
    /// <paramref name="conceptsById"/> must contain, when it exists, the <c>Concept</c> of each
    /// taxonomy (via <c>Taxonomy.ConceptID</c>): <c>FromDate</c>/<c>ToDate</c> come from there
    /// (<c>Taxonomy</c> does not have those columns in Access, but its <c>Concept</c> does, and
    /// everything Access has must be populated). <c>ExcelTemplate</c> still has no source.
    /// </summary>
    private static void LoadTaxonomies(
        SqliteConnection destination,
        IReadOnlyList<AccessTaxonomyRow> selectedTaxonomies,
        IReadOnlyDictionary<int, AccessConceptRow> conceptsById)
    {
        using var writer = new SqliteBatchWriter(
            destination,
            "mTaxonomy",
            ["TaxonomyID", "FrameworkID", "TaxonomyCode", "TaxonomyLabel", "Version", "PublicationDate", "TechnicalStandard", "ConceptID", "FromDate", "ToDate", "ExcelTemplate"]);

        foreach (var taxonomy in selectedTaxonomies)
        {
            AccessConceptRow? concept = taxonomy.ConceptId.HasValue
                && conceptsById.TryGetValue(taxonomy.ConceptId.Value, out var found)
                ? found
                : null;

            writer.AddRow(
                taxonomy.TaxonomyId,
                taxonomy.FrameworkId,
                taxonomy.TaxonomyCode.ToLowerInvariant(), // the target stores the code in lower case
                taxonomy.TaxonomyLabel,
                taxonomy.DpmPackageCode, // Version = DpmPackageCode
                taxonomy.ActualPublicationDate,
                taxonomy.TechnicalStandard,
                taxonomy.ConceptId,
                concept?.FromDate, // from Concept.FromDate (Taxonomy does not have the column)
                concept?.ToDate, // from Concept.ToDate
                null); // ExcelTemplate: no source in Access
        }
    }

    // ------------------------------------------------------------------
    // mRelease: from the dynamic floor upwards, not only the selected ones
    // ------------------------------------------------------------------

    private sealed record ReleaseRow(int ReleaseId, string ReleaseCode, DateTime PublicationDate);

    private static List<ReleaseRow> BuildReleasesFromFloor(ReleaseResolver releaseResolver)
    {
        // ReleaseID is synthetic: Access does not number DpmPackage (it only has the code as PK).
        // ReleasesFromFloor is already ordered by ascending date, so the ReleaseID assigned in
        // that order is reproducible.
        return releaseResolver.ReleasesFromFloor
            .Select((r, index) => new ReleaseRow(index + 1, r.Code, r.Date))
            .ToList();
    }

    private static void LoadReleases(SqliteConnection destination, IReadOnlyList<ReleaseRow> releases)
    {
        using var writer = new SqliteBatchWriter(
            destination,
            "mRelease",
            ["ReleaseID", "ReleaseCode", "ReleaseDescription", "Status", "PublicationDate", "IsCurrent", "ConceptID"]);

        foreach (var release in releases)
        {
            writer.AddRow(
                release.ReleaseId,
                release.ReleaseCode,
                null, // ReleaseDescription: no source in Access
                null, // Status: likewise
                release.PublicationDate,
                true, // IsCurrent = 1
                null); // ConceptID: DpmPackage has no ConceptID in Access
        }
    }

    // ------------------------------------------------------------------
    // mConcept: only the concepts reachable from what the skeleton emits; ReleaseID only for
    // Taxonomy concepts, with the release of their own DpmPackageCode; any other ConceptType
    // (here, ReportingFramework) is emitted with ReleaseID NULL
    // ------------------------------------------------------------------

    private static int LoadConcepts(
        SqliteConnection destination,
        IEnumerable<AccessConceptRow> concepts,
        IReadOnlyDictionary<int, string> releaseCodeByTaxonomyConceptId,
        IReadOnlyDictionary<string, int> releaseIdByCode)
    {
        using var writer = new SqliteBatchWriter(
            destination, "mConcept", ["ConceptID", "ConceptType", "OwnerID", "ReleaseID", "CreationDate", "ModificationDate", "FromDate", "ToDate"]);

        foreach (var concept in concepts)
        {
            int? releaseId = null;

            if (string.Equals(concept.ConceptType, "Taxonomy", StringComparison.Ordinal)
                && releaseCodeByTaxonomyConceptId.TryGetValue(concept.ConceptId, out var releaseCode))
            {
                if (!releaseIdByCode.TryGetValue(releaseCode, out var resolvedReleaseId))
                {
                    // Invariant: the release of a Taxonomy is its own DpmPackageCode, and
                    // mRelease runs from the dynamic floor upwards, so it must always contain
                    // it. If it does not, it is a data error or an error in the floor
                    // computation, and it must stop here instead of silently writing a NULL.
                    throw new InvalidOperationException(
                        $"Could not resolve ReleaseID for ConceptID={concept.ConceptId} "
                        + $"(ConceptType={concept.ConceptType}, derived release='{releaseCode}'). "
                        + "mRelease does not contain that release; check the dynamic floor computation.");
                }

                releaseId = resolvedReleaseId;
            }

            writer.AddRow(
                concept.ConceptId,
                concept.ConceptType,
                concept.OwnerId,
                releaseId,
                concept.CreationDate,
                concept.ModificationDate,
                concept.FromDate,
                concept.ToDate);
        }

        return (int)writer.RowsWritten;
    }

    // ------------------------------------------------------------------
    // Orchestration
    // ------------------------------------------------------------------

    /// <summary>
    /// Loads the seven skeleton tables into <paramref name="destination"/>.
    /// </summary>
    /// <param name="source">Access reader, already open.</param>
    /// <param name="destination">Target SQLite connection, already open on the created schema.</param>
    /// <param name="allTaxonomies">
    /// All the taxonomies in Access, unfiltered. Needed by <see cref="ReleaseResolver"/>: the
    /// release calendar is a global fact, independent of the taxonomy filter of this run.
    /// </param>
    /// <param name="selectedTaxonomies">
    /// The taxonomies resulting from applying the filter (<see cref="TaxonomySelector"/>).
    /// </param>
    /// <param name="absoluteFloorReleaseCode">
    /// Absolute floor. The effective floor of the run can be earlier if the filter selected
    /// taxonomies from a release older than this value.
    /// </param>
    public static Result Load(
        IDpmSourceReader source,
        SqliteConnection destination,
        IReadOnlyList<AccessTaxonomyRow> allTaxonomies,
        IReadOnlyList<AccessTaxonomyRow> selectedTaxonomies,
        string absoluteFloorReleaseCode = ReleaseResolver.AbsoluteFloorReleaseCode)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(allTaxonomies);
        ArgumentNullException.ThrowIfNull(selectedTaxonomies);

        ApplyBulkLoadPragmas(destination);

        LoadOwnerLanguageAndParent(source, destination);

        var reportingFrameworks = LoadReportingFrameworks(source, destination, selectedTaxonomies);

        // Concepts reachable from what has been emitted so far: frameworks (already filtered) +
        // selected taxonomies. They are read once and reused for both
        // mTaxonomy.FromDate/ToDate and mConcept.
        var reachableConceptIds = reportingFrameworks
            .Select(f => f.ConceptId)
            .Concat(selectedTaxonomies.Select(t => t.ConceptId))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var conceptsById = source.ReadConceptsByIds(reachableConceptIds)
            .ToDictionary(c => c.ConceptId);

        LoadTaxonomies(destination, selectedTaxonomies, conceptsById);

        var releaseResolver = new ReleaseResolver(
            allTaxonomies
                .Where(t => t.ActualPublicationDate.HasValue)
                .Select(t => (t.DpmPackageCode, t.ActualPublicationDate!.Value)),
            selectedTaxonomies.Select(t => t.DpmPackageCode),
            absoluteFloorReleaseCode);

        var releases = BuildReleasesFromFloor(releaseResolver);
        LoadReleases(destination, releases);
        var releaseIdByCode = releases.ToDictionary(r => r.ReleaseCode, r => r.ReleaseId, StringComparer.OrdinalIgnoreCase);

        // The release of a Taxonomy concept is that of its own DpmPackageCode.
        var releaseCodeByTaxonomyConceptId = selectedTaxonomies
            .Where(t => t.ConceptId.HasValue)
            .ToDictionary(t => t.ConceptId!.Value, t => t.DpmPackageCode);

        var conceptRows = LoadConcepts(destination, conceptsById.Values, releaseCodeByTaxonomyConceptId, releaseIdByCode);

        // mConceptTranslation for ReportingFramework (FrameworkLabel) and Taxonomy
        // (TaxonomyLabel): the same texts that mReportingFramework/mTaxonomy already emit.
        var translationSeeds = reportingFrameworks
            .Where(f => f.ConceptId.HasValue)
            .Select(f => (f.ConceptId!.Value, (string?)f.FrameworkLabel, (string?)null))
            .Concat(selectedTaxonomies
                .Where(t => t.ConceptId.HasValue)
                .Select(t => (t.ConceptId!.Value, (string?)t.TaxonomyLabel, (string?)null)));
        ConceptTranslationWriter.Write(destination, translationSeeds);

        return new Result(
            OwnerRows: 1 + ConstantOwners.Count,
            OwnerParentRows: 1,
            LanguageRows: 1,
            ReportingFrameworkRows: reportingFrameworks.Count,
            TaxonomyRows: selectedTaxonomies.Count,
            ReleaseRows: releases.Count,
            ConceptRows: conceptRows,
            FloorReleaseCode: releaseResolver.FloorReleaseCode);
    }

    private static void ApplyBulkLoadPragmas(SqliteConnection destination)
    {
        using var command = destination.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = OFF;
            PRAGMA synchronous = OFF;
            PRAGMA foreign_keys = OFF;
            """;
        command.ExecuteNonQuery();
    }
}
