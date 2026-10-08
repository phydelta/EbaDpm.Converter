using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Core.Validation;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Mapping.Dpm20;

/// <summary>
/// Loads the DPM 2.0 skeleton: <c>mRelease</c>, <c>mReportingFramework</c>, <c>mTaxonomy</c> and
/// the five constants (<c>mOwner</c>, <c>mOwnerParent</c>, <c>mLanguage</c>, <c>mRewriteURI</c>,
/// <c>aDatabaseProperties</c>) plus the derived <c>mTaxonomyPackage</c>.
///
/// This is a NEW pipeline, PARALLEL to the DPM 1.0
/// <see cref="EbaDpm.Converter.Core.Mapping.SkeletonLoader"/>: it has the SAME SHAPE by style, but
/// it neither reuses nor depends on it — the business rules differ at several points, starting
/// with the fact that here <c>mTaxonomy.TaxonomyLabel</c> stays NULL whereas in DPM 1.0 it is
/// populated.
///
/// Out of scope here: <c>mConcept</c> and <c>mConceptTranslation</c> (<c>ConceptID</c> stays NULL
/// in everything this class writes) and everything after the skeleton (dictionary, structure,
/// axes/cells, signatures, modules).
/// </summary>
public static class Dpm20SkeletonLoader
{
    /// <summary>Count of rows written per table, for the CLI report.</summary>
    public sealed record Result(
        int ReleaseRows,
        int ReportingFrameworkRows,
        int TaxonomyRows,
        int OwnerRows,
        int OwnerParentRows,
        int LanguageRows,
        int RewriteUriRows,
        int TaxonomyPackageRows,
        int DatabasePropertiesRows);

    // ------------------------------------------------------------------
    // mRelease — the 5 rows of [Release], not filtered by selected taxonomy.
    // ------------------------------------------------------------------

    private static void LoadReleases(SqliteConnection destination, IReadOnlyList<Dpm20ReleaseRow> releases)
    {
        using var writer = new SqliteBatchWriter(
            destination,
            "mRelease",
            ["ReleaseID", "ReleaseCode", "ReleaseDescription", "Status", "PublicationDate", "IsCurrent", "ConceptID"]);

        foreach (var release in releases)
        {
            writer.AddRow(
                release.ReleaseId,
                release.Code,
                release.Description, // Release.Description, multi-line, only exists in 4.1/4.2
                release.Status, // Release.Status ("released" in the first 4, "validation" in 4.2)
                release.Date, // ISO TEXT as is ("2024-02-06"...): do NOT pass through DateTime
                // IsCurrent: DIRECT MAPPING from the source (Release.IsCurrent), without normalizing
                // or forcing any fixed value. It is DYNAMIC from one Access release to the next —
                // as new Access databases are published, the row with IsCurrent=1 changes. That the
                // references seen so far carry 1 in all five rows is a static SNAPSHOT that may
                // coincide with the source at that point in time; it is not a format convention to
                // reproduce here.
                release.IsCurrent,
                (int?)null); // ConceptID: NULL until the concept loader runs
        }
    }

    // ------------------------------------------------------------------
    // mReportingFramework — only the frameworks of the selected taxonomies.
    // ------------------------------------------------------------------

    private static List<Dpm20FrameworkRow> LoadReportingFrameworks(
        SqliteConnection destination,
        IReadOnlyList<Dpm20FrameworkRow> frameworks,
        IReadOnlyList<AccessTaxonomyRow> selectedTaxonomies)
    {
        var emittedFrameworkIds = selectedTaxonomies
            .Where(t => t.FrameworkId.HasValue)
            .Select(t => t.FrameworkId!.Value)
            .ToHashSet();

        var emitted = frameworks
            .Where(f => emittedFrameworkIds.Contains(f.FrameworkId))
            .OrderBy(f => f.FrameworkId)
            .ToList();

        using var writer = new SqliteBatchWriter(
            destination, "mReportingFramework", ["FrameworkID", "FrameworkCode", "FrameworkLabel", "ConceptID"]);

        foreach (var framework in emitted)
        {
            // FrameworkCode/FrameworkLabel neither translated nor case-normalized (matches the reference in 18/18).
            writer.AddRow(framework.FrameworkId, framework.Code, framework.Name, null);
        }

        return emitted;
    }

    // ------------------------------------------------------------------
    // mTaxonomy — TaxonomyLabel/Version/PublicationDate/FromDate/ToDate/ExcelTemplate are NULL,
    // there is no source. TechnicalStandard comes already computed (lowercase framework) from
    // Dpm20TaxonomyDeriver.
    // ------------------------------------------------------------------

    private static void LoadTaxonomies(SqliteConnection destination, IReadOnlyList<AccessTaxonomyRow> selectedTaxonomies)
    {
        using var writer = new SqliteBatchWriter(
            destination,
            "mTaxonomy",
            ["TaxonomyID", "FrameworkID", "TaxonomyCode", "TaxonomyLabel", "Version", "PublicationDate", "TechnicalStandard", "ConceptID", "FromDate", "ToDate", "ExcelTemplate"]);

        foreach (var taxonomy in selectedTaxonomies)
        {
            writer.AddRow(
                taxonomy.TaxonomyId,
                taxonomy.FrameworkId,
                taxonomy.TaxonomyCode, // already lowercase + release (Dpm20TaxonomyDeriver)
                null, // TaxonomyLabel: ALWAYS NULL. There is no Taxonomy table in the source;
                      // filling it with the framework Name would be inventing data.
                null, // Version: no source
                null, // PublicationDate: no source
                taxonomy.TechnicalStandard,
                null, // ConceptID: NULL until the concept loader runs
                null, // FromDate: no source
                null, // ToDate: no source
                null); // ExcelTemplate: no source
        }
    }

    // ------------------------------------------------------------------
    // The constants. Organisation is NOT read (it matches 1 of 3 with mOwner, and not even that
    // one in the code): these five tables are generator constants, as in DPM 1.0, but with their
    // own values.
    // ------------------------------------------------------------------

    private static void LoadOwnerLanguageAndRewriteUri(SqliteConnection destination)
    {
        using (var owners = new SqliteBatchWriter(
            destination,
            "mOwner",
            ["OwnerID", "OwnerName", "OwnerCode", "OwnerNamespace", "OwnerLocation", "OwnerPrefix", "OwnerCopyright", "ParentOwnerID", "ConceptID"]))
        {
            owners.AddRow(1, "European Banking Authority", "eba", "http://www.eba.europa.eu/xbrl/crr/", "http://www.eba.europa.eu/eu/fr/xbrl/crr/", "eba", "(C) EBA", null, null);
            owners.AddRow(2, "Technical", "Technical", "http://technical.info", "http://technical.info", "Technical", null, null, null);
            owners.AddRow(3, "Eurofiling", "eu", "http://www.eurofiling.info/xbrl/", "http://www.eurofiling.info/eu/fr/xbrl/", "eu", "(C) Eurofiling", null, null);
        }

        using (var ownerParent = new SqliteBatchWriter(destination, "mOwnerParent", ["OwnerID", "ParentOwnerID"]))
        {
            // The EBA (1) hangs from Eurofiling (3) — constant, one row.
            ownerParent.AddRow(1, 3);
        }

        using (var language = new SqliteBatchWriter(destination, "mLanguage", ["LanguageID", "LanguageName", "EnglishName", "IsoCode", "ConceptID"]))
        {
            // Constant, one row: English only, with no name in either language column.
            language.AddRow(1, null, null, "en", null);
        }

        using (var rewriteUri = new SqliteBatchWriter(destination, "mRewriteURI", ["RewriteUriID", "Local", "Absolute", "TaxonomyPackageID"]))
        {
            // RewriteUriID starts at 2, not 1 (as measured in the reference). TaxonomyPackageID=1:
            // the only row of mTaxonomyPackage (LoadTaxonomyPackage below).
            rewriteUri.AddRow(2, "../www.eba.europa.eu/", "http://www.eba.europa.eu/", 1);
            rewriteUri.AddRow(3, "../www.w3.org/", "http://www.w3.org/", 1);
            rewriteUri.AddRow(4, "../www.xbrl.org/", "http://www.xbrl.org/", 1);
            rewriteUri.AddRow(5, "../www.eurofiling.info/", "http://www.eurofiling.info/", 1);
        }

        using (var databaseProperties = new SqliteBatchWriter(destination, "aDatabaseProperties", ["Property", "Value"]))
        {
            databaseProperties.AddRow("Validation syntax version", "2");

            // The mark that --validate reads to know the SCOPE of the checks. The DPM 1.0 pipeline
            // does NOT write this row (ValidationSourceModelDetector.Detect): its absence means
            // DPM 1.0, and that is why the output of that pipeline does not change by a single byte.
            databaseProperties.AddRow(ValidationSourceModelDetector.PropertyName, ValidationSourceModelDetector.Dpm2Value);
        }
    }

    // ------------------------------------------------------------------
    // mTaxonomyPackage — derived, one row, emitted COMPLETE. It does NOT exist in the source
    // (there is no TaxonomyPackage table): PublicationDate comes from the cutoff release,
    // Name/Description are derived from the frameworks ACTUALLY emitted in this run (not from the
    // 20 in Access). The remaining columns are FIXED generator metadata — the same class as
    // mOwner. The rule "tables without a source are created empty" applies to TABLES, not to
    // leaving half a row NULL to avoid "inventing" the rest. Two columns (LicenceName/LicenceHref)
    // carry an EMPTY string and not NULL: that is what the reference has, and it is reproduced
    // as is.
    // ------------------------------------------------------------------

    private static void LoadTaxonomyPackage(
        SqliteConnection destination,
        string cutoffReleaseCode,
        string? cutoffReleaseDate,
        IReadOnlyList<Dpm20FrameworkRow> emittedFrameworks)
    {
        var codesJoined = string.Join(
            "_",
            emittedFrameworks
                .Select(f => f.Code.ToUpperInvariant())
                .OrderBy(c => c, StringComparer.Ordinal));

        using var writer = new SqliteBatchWriter(
            destination,
            "mTaxonomyPackage",
            ["TaxonomyPackageID", "Lang", "SchemaLocation", "Identifier", "Description", "Name", "Version", "Publisher", "PublisherURL", "PublisherCountry", "PublicationDate", "LicenceName", "LicenceHref", "CopyrightComment"]);

        // Version = cutoff release code + ".0.0". Rule based on a SINGLE SAMPLE (4.2 → "4.2.0.0"):
        // it fits, but there is no second reference to contrast it with.
        var version = $"{cutoffReleaseCode}.0.0";

        // Identifier: SAME single-sample rule as Version — it interpolates release and version into
        // the template measured on the reference database. It reproduces exactly the reference
        // string for 4.2 because that is the sample, not because it is proven for another release.
        var identifier = $"http://www.eba.europa.eu/eu/fr/xbrl/tp/{cutoffReleaseCode}/EBA_XBRL_{cutoffReleaseCode}_Reporting_Frameworks_{version}.zip";

        // SAME single-sample rule as Version/Identifier: Name/Description are NOT just the list of
        // codes (codesJoined) — they go inside a text template, with the release and version
        // interpolated from the same source as Identifier and Version. It fits the only reference
        // that has it (4.2); there is nothing to contrast it with in another release.
        var name = $"EBA XBRL {cutoffReleaseCode} Reporting Frameworks {version} ({codesJoined})";
        var description = $"{name}. Requires Dictionary {cutoffReleaseCode} or later";

        writer.AddRow(
            1,
            null, // Lang: NULL — the reference has it that way too
            // SchemaLocation: FIXED metadata of the XBRL taxonomy package format, not of the DPM
            // source: mTaxonomyPackage is emitted complete, like mOwner.
            "http://xbrl.org/2016/taxonomy-package http://www.xbrl.org/2016/taxonomy-package.xsd",
            identifier,
            description, // Description: template + ". Requires Dictionary {release} or later" (single sample)
            name, // Name: template with the list of emitted codes interpolated (single sample)
            version,
            "European Banking Authority", // Publisher: generator metadata
            "http://www.eba.europa.eu/", // PublisherURL: generator metadata
            null, // PublisherCountry: NULL — the reference has it that way too
            cutoffReleaseDate, // PublicationDate = Release.Date of the cutoff release (verified: "2025-10-31")
            string.Empty, // LicenceName: EMPTY string, not NULL — as measured in the reference
            string.Empty, // LicenceHref: EMPTY string, not NULL — as measured in the reference
            null); // CopyrightComment: NULL — the reference has it that way too
    }

    // ------------------------------------------------------------------
    // Orchestration
    // ------------------------------------------------------------------

    /// <param name="releases">The 5 rows of <c>[Release]</c>, unfiltered (it is not versioned).</param>
    /// <param name="frameworks">The source <c>Framework</c> rows, unfiltered (20 rows).</param>
    /// <param name="selectedTaxonomies">
    /// The taxonomies resulting from applying the selection filter to those derived by
    /// <see cref="Dpm20TaxonomyDeriver"/>.
    /// </param>
    /// <param name="cutoffReleaseCode"><c>Dpm20AccessReader.CutoffReleaseCode</c>.</param>
    /// <param name="destination">Destination SQLite connection, already open on the created schema.</param>
    public static Result Load(
        IReadOnlyList<Dpm20ReleaseRow> releases,
        IReadOnlyList<Dpm20FrameworkRow> frameworks,
        IReadOnlyList<AccessTaxonomyRow> selectedTaxonomies,
        string cutoffReleaseCode,
        SqliteConnection destination)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(frameworks);
        ArgumentNullException.ThrowIfNull(selectedTaxonomies);
        ArgumentException.ThrowIfNullOrWhiteSpace(cutoffReleaseCode);
        ArgumentNullException.ThrowIfNull(destination);

        ApplyBulkLoadPragmas(destination);

        LoadReleases(destination, releases);

        var emittedFrameworks = LoadReportingFrameworks(destination, frameworks, selectedTaxonomies);

        LoadTaxonomies(destination, selectedTaxonomies);

        // mTaxonomyPackage before mOwner/.../mRewriteURI: mRewriteURI.TaxonomyPackageID references
        // it (with foreign_keys=OFF the order is not mandatory, but it is kept for clarity).
        var cutoffRelease = releases.FirstOrDefault(
            r => string.Equals(r.Code, cutoffReleaseCode, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"The cutoff release '{cutoffReleaseCode}' is not among those read from [Release]; "
                + "mTaxonomyPackage.PublicationDate cannot be derived.");

        LoadTaxonomyPackage(destination, cutoffReleaseCode, cutoffRelease.Date, emittedFrameworks);

        LoadOwnerLanguageAndRewriteUri(destination);

        return new Result(
            ReleaseRows: releases.Count,
            ReportingFrameworkRows: emittedFrameworks.Count,
            TaxonomyRows: selectedTaxonomies.Count,
            OwnerRows: 3,
            OwnerParentRows: 1,
            LanguageRows: 1,
            RewriteUriRows: 4,
            TaxonomyPackageRows: 1,
            DatabasePropertiesRows: 2);
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
