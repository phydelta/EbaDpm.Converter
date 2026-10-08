using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Core.Mapping;
using EbaDpm.Converter.Core.Mapping.Dpm20;
using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Core.Validation;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Cli;

/// <summary>
/// Logical entry point of the CLI. Arguments are parsed by hand, with no external dependencies.
///
/// Exit codes:
///   0 = success (even if there are informative differences, or plane B was skipped).
///   1 = missing or invalid arguments.
///   2 = recognized mode that is not implemented yet.
///   3 = --validate: there are critical differences.
/// </summary>
public static class CliRunner
{
    public const int ExitOk = 0;
    public const int ExitArgumentError = 1;
    public const int ExitNotImplemented = 2;
    public const int ExitValidationCriticalDifferences = 3;

    private static readonly string[] HelpFlags = ["--help", "-h", "-?", "/?"];

    private const string Usage =
        """
        EbaDpm.Converter --source "<path.accdb>" --output "<path.db>"
                         (--taxonomies "..." | --taxonomykeys "..." | --releases "..." | --all)
                         [--cutoff-release <code>] [--overwrite] [--report <path.json>] [--verbose]

        EbaDpm.Converter --list-taxonomies --source "<path.accdb>" [--cutoff-release <code>]
        EbaDpm.Converter --schema-only --output "<path.db>"
        EbaDpm.Converter --validate "<generated.db>" [--reference "<ref.db>"] [--layouts "<layouts-repository.db>"] [--report <path.json>]
        EbaDpm.Converter --extract-layouts "<directory>" --out "<repository.db>"
                         --dictionary "<dictionary.db>" [--overwrite]
        EbaDpm.Converter --help | -h | -? | /?

        Taxonomy selectors (exactly ONE is required):
          --taxonomies "COREP 3.2,IF 3.2"   by TaxonomyCode (the code, as stored in mTaxonomy.TaxonomyCode)
          --taxonomykeys "..."              by TaxonomyKey (a derived identifier, not a stored value)
                                             e.g.: corep/its-005-2020/2022-03-01 (up to 3.x), sbp/4.0 (from 4.0)
          --releases 4.0,4.1                by DpmPackageCode
          --all                             all taxonomies

        --list-taxonomies shows both columns (TaxonomyKey and TaxonomyCode) in case you do not know what to type.

        --cutoff-release <code>: DPM 2.0 sources only. Converts the database as of the release with that
        Release.Code (e.g. 4.2). Without it, the cutoff is the latest release declared in the database.

        --validate: --reference is OPTIONAL. Without it, only plane A is run (internal invariants).
        With it, plane B is added (semantic diff against the reference SQLite database).
        --layouts is OPTIONAL and INDEPENDENT of --reference: it adds plane C, the signature-by-signature
        comparison against the Annotated Table Layouts repository (the one produced by --extract-layouts).
        Without it, plane C does NOT run and the report says so explicitly - never silently.

        --extract-layouts: dumps the Annotated Table Layouts (.xlsx) found recursively under <directory>
        into a queryable SQLite repository. --dictionary is REQUIRED: it points to a SQLite database
        with the distribution schema (mDimension/mDomain/mMember) used to disambiguate a single-term
        "(code)" between a metric and a key dimension, and to validate "(domain:member)"; without a
        dictionary both classifications would be a fallback that mislabels cells in an
        indistinguishable way, so the CLI refuses instead of risking it. Its path and SHA-256 are
        recorded in LayoutExtraction (provenance).

        Exit codes: 0 = success. 1 = missing or invalid arguments. 2 = mode not implemented. 3 = critical differences.
        """;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Any(a => HelpFlags.Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            stdout.WriteLine(Usage);
            return ExitOk;
        }

        if (args.Length == 0)
        {
            stderr.WriteLine("Error: no arguments given.");
            stderr.WriteLine("Use --help to see the usage.");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return ExitArgumentError;
        }

        if (HasFlag(args, "--schema-only"))
        {
            return RunSchemaOnly(args, stdout, stderr);
        }

        if (HasFlag(args, "--list-taxonomies"))
        {
            return RunListTaxonomies(args, stdout, stderr);
        }

        if (HasFlag(args, "--validate"))
        {
            return RunValidate(args, stdout, stderr);
        }

        if (HasFlag(args, "--extract-layouts"))
        {
            return RunExtractLayouts(args, stdout, stderr);
        }

        // Any other combination of arguments points to the full conversion mode.
        return RunConvert(args, stdout, stderr);
    }

    private static int RunListTaxonomies(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (!TryGetOptionValue(args, "--source", out var source, out var error))
        {
            stderr.WriteLine($"Error: {error}");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return ExitArgumentError;
        }

        var recognized = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--list-taxonomies", "--source", "--cutoff-release",
        };
        var unknown = FindUnknownTokens(args, recognized, optionsWithValue: ["--source", "--cutoff-release"]);
        if (unknown.Count > 0)
        {
            stderr.WriteLine($"Error: unrecognized argument in --list-taxonomies mode: {unknown[0]}");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return ExitArgumentError;
        }

        if (!TryGetCutoffRelease(args, out var cutoffReleaseCode, out var cutoffError))
        {
            stderr.WriteLine($"Error: {cutoffError}");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return ExitArgumentError;
        }

        try
        {
            // Single branching point. The detected model is always printed.
            var sourceModel = SourceModelDetector.Detect(source);

            if (sourceModel == DpmSourceModel.Dpm10)
            {
                if (cutoffReleaseCode is not null)
                {
                    stderr.WriteLine($"Error: {CutoffReleaseDpm10Message}");
                    return ExitArgumentError;
                }

                stdout.WriteLine("Detected source model: DPM 1.0.");
                stdout.WriteLine();
                return RunListTaxonomiesDpm10(source, stdout);
            }

            return RunListTaxonomiesDpm20(source, cutoffReleaseCode, stdout);
        }
        catch (SourceModelDetectionException ex)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return ExitArgumentError;
        }
        catch (TaxonomyKeyResolutionException ex)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return ExitArgumentError;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return ExitArgumentError;
        }
    }

    private static int RunListTaxonomiesDpm10(string source, TextWriter stdout)
    {
        using var reader = new Dpm10AccessReader(source);
        reader.Open();

        var taxonomies = reader.ReadTaxonomies().ToList();
        var tableCounts = reader.ReadTaxonomyTableCounts()
            .ToDictionary(c => c.TaxonomyId, c => c.TableCount);
        var taxonomyKeyByTaxonomyId = TaxonomyKeyResolver.Resolve(reader.ReadModules());

        // --list-taxonomies shows the TaxonomyKey as the main identifier.
        WriteTaxonomyTableHeader(stdout);
        foreach (var taxonomy in taxonomies.OrderBy(t => t.TaxonomyCode, StringComparer.OrdinalIgnoreCase))
        {
            var tableCount = tableCounts.GetValueOrDefault(taxonomy.TaxonomyId);
            var publicationDate = taxonomy.ActualPublicationDate?.ToString("yyyy-MM-dd") ?? "-";
            var taxonomyKey = taxonomyKeyByTaxonomyId.GetValueOrDefault(taxonomy.TaxonomyId, "-");
            WriteTaxonomyRow(stdout, taxonomyKey, taxonomy.TaxonomyCode, taxonomy.DpmPackageCode, publicationDate, tableCount);
        }

        stdout.WriteLine();
        stdout.WriteLine($"Total: {taxonomies.Count} taxonomies.");
        return ExitOk;
    }

    private const string CutoffReleaseDpm10Message = "--cutoff-release only applies to DPM 2.0 sources.";

    /// <summary>
    /// Reads the optional <c>--cutoff-release</c> value. Absent is fine (null); present without a
    /// value is an error.
    /// </summary>
    private static bool TryGetCutoffRelease(string[] args, out string? cutoffReleaseCode, out string error)
    {
        cutoffReleaseCode = null;
        error = string.Empty;

        if (!HasFlag(args, "--cutoff-release"))
        {
            return true;
        }

        if (!TryGetOptionValue(args, "--cutoff-release", out var value, out error))
        {
            return false;
        }

        cutoffReleaseCode = value.Trim();
        return true;
    }

    private static string DescribeCutoff(Dpm20AccessReader reader)
        => reader.CutoffReleaseRequested ? $"{reader.CutoffReleaseCode}, requested" : reader.CutoffReleaseCode;

    private static int RunListTaxonomiesDpm20(string source, string? cutoffReleaseCode, TextWriter stdout)
    {
        using var reader = new Dpm20AccessReader(source, cutoffReleaseCode: cutoffReleaseCode);
        reader.Open();

        stdout.WriteLine($"Detected source model: DPM 2.0 (cutoff release {DescribeCutoff(reader)}).");
        stdout.WriteLine();

        var releases = reader.ReadReleases().ToList();
        var frameworks = reader.ReadFrameworks().ToList();
        var modules = reader.ReadModules().ToList();
        var moduleVersions = reader.ReadModuleVersions().ToList();
        var compositions = reader.ReadModuleVersionCompositions().ToList();
        var tableVersions = reader.ReadTableVersions().ToList();

        var derivation = Dpm20TaxonomyDeriver.Derive(
            releases, frameworks, modules, moduleVersions, compositions, tableVersions, reader.CutoffReleaseId);

        var tableCounts = derivation.TableCounts.ToDictionary(c => c.TaxonomyId, c => c.TableCount);

        // The DPM 2.0 taxonomy is derived; it carries no publication date.
        WriteTaxonomyTableHeader(stdout);
        foreach (var taxonomy in derivation.Taxonomies.OrderBy(t => t.TaxonomyCode, StringComparer.Ordinal))
        {
            var tableCount = tableCounts.GetValueOrDefault(taxonomy.TaxonomyId);
            var taxonomyKey = derivation.TaxonomyKeyByTaxonomyId.GetValueOrDefault(taxonomy.TaxonomyId, "-");
            WriteTaxonomyRow(stdout, taxonomyKey, taxonomy.TaxonomyCode, taxonomy.DpmPackageCode, "-", tableCount);
        }

        stdout.WriteLine();
        stdout.WriteLine($"Total: {derivation.Taxonomies.Count} taxonomies.");
        return ExitOk;
    }

    private static void WriteTaxonomyTableHeader(TextWriter stdout)
        => stdout.WriteLine($"{"TaxonomyKey",-45} {"Code",-30} {"DPM package",-12} {"Published",-12} {"Tables",6}");

    private static void WriteTaxonomyRow(
        TextWriter stdout, string taxonomyKey, string taxonomyCode, string dpmPackageCode, string publicationDate, int tableCount)
        => stdout.WriteLine($"{taxonomyKey,-45} {taxonomyCode,-30} {dpmPackageCode,-12} {publicationDate,-12} {tableCount,6}");

    private static int RunConvert(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var recognized = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--source", "--output", "--taxonomies", "--taxonomykeys", "--releases", "--all", "--overwrite", "--report", "--verbose",
            "--cutoff-release",
        };
        var optionsWithValue = new[] { "--source", "--output", "--taxonomies", "--taxonomykeys", "--releases", "--report", "--cutoff-release" };
        var unknown = FindUnknownTokens(args, recognized, optionsWithValue);
        if (unknown.Count > 0)
        {
            stderr.WriteLine($"Error: unrecognized argument: {unknown[0]}");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return ExitArgumentError;
        }

        if (!TryGetOptionValue(args, "--source", out var source, out var sourceError))
        {
            stderr.WriteLine($"Error: {sourceError}");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return ExitArgumentError;
        }

        if (!TryGetOptionValue(args, "--output", out var output, out var outputError))
        {
            stderr.WriteLine($"Error: {outputError}");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return ExitArgumentError;
        }

        if (!TryGetCutoffRelease(args, out var cutoffReleaseCode, out var cutoffError))
        {
            stderr.WriteLine($"Error: {cutoffError}");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return ExitArgumentError;
        }

        var overwrite = HasFlag(args, "--overwrite");
        var all = HasFlag(args, "--all");
        var taxonomyCodes = TryGetOptionValue(args, "--taxonomies", out var taxonomiesValue, out _)
            ? SplitCsv(taxonomiesValue)
            : null;
        var taxonomyKeys = TryGetOptionValue(args, "--taxonomykeys", out var taxonomyKeysValue, out _)
            ? SplitCsv(taxonomyKeysValue)
            : null;
        var releaseCodes = TryGetOptionValue(args, "--releases", out var releasesValue, out _)
            ? SplitCsv(releasesValue)
            : null;

        try
        {
            // Single branching point. The detected model is always printed.
            var sourceModel = SourceModelDetector.Detect(source);
            stdout.WriteLine(
                sourceModel == DpmSourceModel.Dpm10
                    ? "Detected source model: DPM 1.0."
                    : "Detected source model: DPM 2.0.");

            if (sourceModel == DpmSourceModel.Dpm20)
            {
                return RunConvertDpm20(
                    source, output, overwrite, taxonomyCodes, taxonomyKeys, releaseCodes, all, cutoffReleaseCode, stdout, stderr);
            }

            if (cutoffReleaseCode is not null)
            {
                stderr.WriteLine($"Error: {CutoffReleaseDpm10Message}");
                return ExitArgumentError;
            }

            SchemaCreator.Create(output, overwrite);

            using var accessReader = new Dpm10AccessReader(source);
            accessReader.Open();

            var allTaxonomies = accessReader.ReadTaxonomies().ToList();
            var taxonomyKeyByTaxonomyId = TaxonomyKeyResolver.Resolve(accessReader.ReadModules());
            var request = new TaxonomySelectionRequest(taxonomyCodes, taxonomyKeys, releaseCodes, all);
            var selectedTaxonomies = TaxonomySelector.Resolve(allTaxonomies, request, taxonomyKeyByTaxonomyId);

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = output,
                Mode = SqliteOpenMode.ReadWrite,
            }.ToString();

            using var destination = new SqliteConnection(connectionString);
            destination.Open();

            var skeleton = SkeletonLoader.Load(accessReader, destination, allTaxonomies, selectedTaxonomies);
            var dictionary = DictionaryLoader.Load(accessReader, destination, allTaxonomies, selectedTaxonomies);
            var templates = TemplateOrTableLoader.Load(accessReader, destination, selectedTaxonomies);
            var axesAndCells = AxisAndCellLoader.Load(accessReader, destination, templates.TableIdByTableVId);
            var modules = ModuleLoader.Load(accessReader, destination, selectedTaxonomies);

            // Final VACUUM: the reproducibility requirement is on CONTENT, not on the file, and
            // without VACUUM the size of the .db varies between identical runs because of the
            // batched write pattern and the SQLite page cache. VACUUM cannot run inside a
            // transaction: none is left open here, each SqliteBatchWriter commits and closes its
            // own when it completes.
            var vacuumStopwatch = System.Diagnostics.Stopwatch.StartNew();
            using (var vacuumCommand = destination.CreateCommand())
            {
                vacuumCommand.CommandText = "VACUUM;";
                vacuumCommand.ExecuteNonQuery();
            }
            vacuumStopwatch.Stop();

            stdout.WriteLine(
                $"Skeleton loaded into '{output}': {selectedTaxonomies.Count} taxonomies, "
                + $"{skeleton.ReportingFrameworkRows} frameworks, {skeleton.ReleaseRows} releases (floor {skeleton.FloorReleaseCode}), "
                + $"{skeleton.ConceptRows} concepts.");
            stdout.WriteLine(
                $"Dictionary loaded: {dictionary.DomainRows} domains, {dictionary.MemberRows} members, "
                + $"{dictionary.DimensionRows} dimensions, {dictionary.MetricRows} metrics, "
                + $"{dictionary.HierarchyRows} hierarchies, {dictionary.HierarchyNodeRows} nodes, "
                + $"{dictionary.NewConceptRows} new concepts, {dictionary.ConceptTranslationRows} translations.");
            stdout.WriteLine(
                $"Templates and tables loaded: {templates.TemplateOrTableRows} template/table nodes, "
                + $"{templates.TableRows} tables, {templates.TaxonomyTableRows} taxonomy-table links.");
            stdout.WriteLine(
                $"Axes and cells loaded: {axesAndCells.AxisRows} axes, {axesAndCells.AxisOrdinateRows} ordinates, "
                + $"{axesAndCells.OrdinateCategorisationRows} categorisations, {axesAndCells.TableCellRows} cells, "
                + $"{axesAndCells.CellPositionRows} positions, {axesAndCells.OpenAxisValueRestrictionRows} open-axis "
                + $"restrictions, {axesAndCells.SkippedDefaultMemberResets} resets skipped, "
                + $"{axesAndCells.MetricInheritedFromAncestorCellCount} cells with a metric inherited from an "
                + "ancestor (expected 0).");
            stdout.WriteLine(
                $"Sentinel categorisations: {axesAndCells.TransferredSentinelFixedPairs} fixed categorisations of the "
                + "sentinel transferred to their target ordinate (1:1, or 1:2 on a single dimension), "
                + $"{axesAndCells.AmbiguousSentinelFixedPairSkips} sentinels with a partition shape "
                + "other than those two, left undecided.");
            foreach (var detail in axesAndCells.AmbiguousSentinelFixedPairDetails)
            {
                stdout.WriteLine($"  Undecided sentinel categorisation: {detail}");
            }
            stdout.WriteLine(
                $"Modules loaded: {modules.ModuleRows} modules, {modules.ConceptualModuleRows} conceptual modules, "
                + $"{modules.ModuleBusinessTemplateRows} module-template links.");
            stdout.WriteLine($"Final VACUUM: {vacuumStopwatch.ElapsedMilliseconds} ms.");
            return ExitOk;
        }
        catch (SourceModelDetectionException ex)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return ExitArgumentError;
        }
        catch (TaxonomySelectionException ex)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return ExitArgumentError;
        }
        catch (TaxonomyKeyResolutionException ex)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return ExitArgumentError;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return ExitArgumentError;
        }
    }

    /// <summary>
    /// DPM 2.0 branch of <see cref="RunConvert"/>. Creates the schema and loads, in order, the
    /// skeleton (<see cref="Dpm20SkeletonLoader"/>), the full dictionary
    /// (<see cref="Dpm20DictionaryLoader"/>), the hierarchy tree
    /// (<see cref="Dpm20HierarchyNodeLoader"/>: <c>mHierarchyNode</c>), the structure
    /// (<see cref="Dpm20StructureLoader"/>), the axes/cells/signatures
    /// (<see cref="Dpm20AxisAndCellLoader"/>), the modules (<see cref="Dpm20ModuleLoader"/>:
    /// <c>mModule</c>, <c>mConceptualModule</c>, <c>mModuleBusinessTemplate</c>) and, in a FINAL
    /// phase of its own after all the previous loaders, the concepts
    /// (<see cref="Dpm20ConceptLoader"/>: <c>mConcept</c>, <c>mConceptTranslation</c>).
    /// </summary>
    private static int RunConvertDpm20(
        string source,
        string output,
        bool overwrite,
        List<string>? taxonomyCodes,
        List<string>? taxonomyKeys,
        List<string>? releaseCodes,
        bool all,
        string? cutoffReleaseCode,
        TextWriter stdout,
        TextWriter stderr)
    {
        using var accessReader = new Dpm20AccessReader(source, cutoffReleaseCode: cutoffReleaseCode);
        accessReader.Open();

        stdout.WriteLine(
            accessReader.CutoffReleaseRequested
                ? $"Cutoff release: {accessReader.CutoffReleaseCode} (requested)."
                : $"Cutoff release: {accessReader.CutoffReleaseCode}.");

        var releases = accessReader.ReadReleases().ToList();
        var frameworks = accessReader.ReadFrameworks().ToList();
        var modules = accessReader.ReadModules().ToList();
        var moduleVersions = accessReader.ReadModuleVersions().ToList();
        var compositions = accessReader.ReadModuleVersionCompositions().ToList();
        var tableVersions = accessReader.ReadTableVersions().ToList();

        var derivation = Dpm20TaxonomyDeriver.Derive(
            releases, frameworks, modules, moduleVersions, compositions, tableVersions, accessReader.CutoffReleaseId);

        var request = new TaxonomySelectionRequest(taxonomyCodes, taxonomyKeys, releaseCodes, all);
        var selectedTaxonomies = TaxonomySelector.Resolve(derivation.Taxonomies, request, derivation.TaxonomyKeyByTaxonomyId);

        SchemaCreator.Create(output, overwrite);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = output,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString();

        using var destination = new SqliteConnection(connectionString);
        destination.Open();

        var skeleton = Dpm20SkeletonLoader.Load(releases, frameworks, selectedTaxonomies, accessReader.CutoffReleaseCode, accessReader.CutoffReleaseId, destination);

        var dictionary = Dpm20DictionaryLoader.Load(accessReader, destination, out var dictionaryDiagnostics);

        var hierarchyNodes = Dpm20HierarchyNodeLoader.Load(accessReader, destination, dictionary.HierarchyBySubCategoryId);

        var structure = Dpm20StructureLoader.Load(accessReader, destination, derivation, tableVersions, selectedTaxonomies);

        var axesAndCells = Dpm20AxisAndCellLoader.Load(
            accessReader, destination, structure.TableVIdByTableId, tableVersions, dictionary.HierarchyBySubCategoryId);

        var moduleResult = Dpm20ModuleLoader.Load(
            accessReader, destination, modules, moduleVersions, compositions, tableVersions, frameworks,
            selectedTaxonomies, structure);

        // FINAL phase, after the six previous loaders. Mints mConcept and mConceptTranslation by
        // reading what has already been written; it does not touch any of the six loaders above.
        var concepts = Dpm20ConceptLoader.Load(destination);

        var vacuumStopwatch = System.Diagnostics.Stopwatch.StartNew();
        using (var vacuumCommand = destination.CreateCommand())
        {
            vacuumCommand.CommandText = "VACUUM;";
            vacuumCommand.ExecuteNonQuery();
        }
        vacuumStopwatch.Stop();

        stdout.WriteLine(
            $"DPM 2.0 skeleton loaded into '{output}': {skeleton.ReleaseRows} releases, "
            + $"{skeleton.ReportingFrameworkRows} frameworks, {skeleton.TaxonomyRows} taxonomies, "
            + $"{skeleton.OwnerRows} owners, {skeleton.OwnerParentRows} owner-parent, "
            + $"{skeleton.LanguageRows} languages, {skeleton.RewriteUriRows} rewrite-URI, "
            + $"{skeleton.TaxonomyPackageRows} taxonomy package, {skeleton.DatabasePropertiesRows} properties.");
        stdout.WriteLine(
            $"DPM 2.0 dictionary loaded: {dictionary.DomainRows} domains, "
            + $"{dictionary.DomainUnionRows} domain unions, {dictionary.MemberRows} members, "
            + $"{dictionary.MetricRows} metrics, {dictionary.DimensionRows} dimensions, "
            + $"{dictionary.HierarchyRows} hierarchies.");
        WriteDictionaryDiagnostics(stdout, dictionaryDiagnostics);
        stdout.WriteLine(
            $"DPM 2.0 hierarchy tree loaded: {hierarchyNodes.HierarchyNodeRows} "
            + $"mHierarchyNode, {hierarchyNodes.HierarchiesWithoutNodes} hierarchies without nodes "
            + "(no items in their effective version).");
        WriteDiagnosticList(stdout, "Hierarchy tree: MemberID anchored to its own domain", hierarchyNodes.AnchoredMembers);
        stdout.WriteLine(
            $"DPM 2.0 structure loaded: {structure.TableRows} mTable, "
            + $"{structure.TaxonomyTableRows} mTaxonomyTable, {structure.TemplateOrTableRows} "
            + $"mTemplateOrTable ({structure.TableGroupLevel1Rows} group + {structure.TemplateLevel2Rows} "
            + $"template + {structure.BusinessTableRows} table). Invariant mTaxonomyTable == "
            + $"BusinessTable: {(structure.PairInvariantHolds ? "holds" : "DOES NOT HOLD")}.");
        WriteDiagnosticList(stdout, "Structure: unresolved cases", structure.UnresolvedCases);
        stdout.WriteLine(
            $"DPM 2.0 axes and cells loaded: {axesAndCells.AxisRows} mAxis, "
            + $"{axesAndCells.TableAxisRows} mTableAxis, {axesAndCells.AxisOrdinateRows} mAxisOrdinate, "
            + $"{axesAndCells.TableCellRows} mTableCell, {axesAndCells.CellPositionRows} mCellPosition, "
            + $"{axesAndCells.OrdinateCategorisationRows} mOrdinateCategorisation (informative, not "
            + $"compared by count), {axesAndCells.OpenAxisValueRestrictionRows} "
            + "mOpenAxisValueRestriction.");
        stdout.WriteLine(
            $"  Categorisation: {axesAndCells.UnresolvedContextPairs} unresolved context pairs, "
            + $"{axesAndCells.UnresolvedMetricPairs} unresolved metric pairs, "
            + $"{axesAndCells.UnresolvedOpenAxisDimensions} unresolved open-axis dimensions, "
            + $"{axesAndCells.UnresolvedOpenAxisRestrictions} unresolved open-axis restrictions.");
        WriteDiagnosticList(stdout, "Axes and cells: structural anomalies", axesAndCells.StructuralAnomalies);
        stdout.WriteLine(
            $"DPM 2.0 modules loaded: {moduleResult.ModuleRows} mModule, "
            + $"{moduleResult.ConceptualModuleRows} mConceptualModule, "
            + $"{moduleResult.ModuleBusinessTemplateRows} mModuleBusinessTemplate "
            + "(Order: a declared choice, not derived).");
        WriteDiagnosticList(stdout, "Modules: unresolved cases", moduleResult.UnresolvedCases);
        stdout.WriteLine(
            $"DPM 2.0 concepts minted: {concepts.ConceptRows} mConcept, "
            + $"{concepts.ConceptTranslationRows} mConceptTranslation, {concepts.ConceptsWithReleaseId} "
            + "with ReleaseID.");
        foreach (var type in concepts.ConceptRowsByType.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            stdout.WriteLine($"  {type.Key,-18} {type.Value,7}");
        }

        stdout.WriteLine($"Final VACUUM: {vacuumStopwatch.ElapsedMilliseconds} ms.");
        return ExitOk;
    }

    /// <summary>
    /// The named cases that are documented as not fully resolved: they are listed, not hidden and
    /// not forced.
    /// </summary>
    private static void WriteDictionaryDiagnostics(
        TextWriter stdout, EbaDpm.Converter.Core.Mapping.Dpm20.Dpm20DictionaryLoader.Diagnostics diagnostics)
    {
        WriteDiagnosticList(stdout, "mMetric.DataType without a known translation", diagnostics.UnresolvedMetricDataTypeCases);
        WriteDiagnosticList(stdout, "mDimension without its own code in ItemCategory/_PR", diagnostics.DimensionsWithoutCode);
        WriteDiagnosticList(stdout, "mDimension without a row in PropertyCategory", diagnostics.DimensionsWithoutPropertyCategory);
        WriteDiagnosticList(stdout, "mDimension.DomainID unresolved", diagnostics.UnresolvedDimensionDomainCases);
    }

    private static void WriteDiagnosticList(TextWriter stdout, string title, IReadOnlyList<string> cases)
    {
        if (cases.Count == 0)
        {
            return;
        }

        stdout.WriteLine($"  {title}: {cases.Count} case(s).");
        foreach (var item in cases.Take(20))
        {
            stdout.WriteLine($"    - {item}");
        }

        if (cases.Count > 20)
        {
            stdout.WriteLine($"    ... and {cases.Count - 20} more.");
        }
    }

    private static List<string> SplitCsv(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static int RunSchemaOnly(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (!TryGetOptionValue(args, "--output", out var output, out var error))
        {
            stderr.WriteLine($"Error: {error}");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return ExitArgumentError;
        }

        var overwrite = HasFlag(args, "--overwrite");

        var recognized = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--schema-only", "--output", "--overwrite",
        };
        var unknown = FindUnknownTokens(args, recognized, optionsWithValue: ["--output"]);
        if (unknown.Count > 0)
        {
            stderr.WriteLine($"Error: unrecognized argument in --schema-only mode: {unknown[0]}");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return ExitArgumentError;
        }

        try
        {
            var tableCount = SchemaCreator.Create(output, overwrite);
            stdout.WriteLine($"Schema created in '{output}': {tableCount} tables.");
            return ExitOk;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return ExitArgumentError;
        }
    }

    private static int RunExtractLayouts(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (!TryGetOptionValue(args, "--extract-layouts", out var inputDirectory, out var error))
        {
            stderr.WriteLine($"Error: {error}");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return ExitArgumentError;
        }

        if (!TryGetOptionValue(args, "--out", out var outputPath, out var outputError))
        {
            stderr.WriteLine($"Error: {outputError}");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return ExitArgumentError;
        }

        var recognized = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--extract-layouts", "--out", "--dictionary", "--overwrite",
        };
        var unknown = FindUnknownTokens(args, recognized, optionsWithValue: ["--extract-layouts", "--out", "--dictionary"]);
        if (unknown.Count > 0)
        {
            stderr.WriteLine($"Error: unrecognized argument in --extract-layouts mode: {unknown[0]}");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return ExitArgumentError;
        }

        // --dictionary is REQUIRED on the CLI. Without it a single-term "(code)" cannot be
        // classified (metric vs key dimension) nor can "(domain:member)" be validated - and a
        // fallback that mislabels hundreds of cells is worse than refusing. (The library,
        // LayoutExtractor.Extract, keeps dictionaryPath optional: several tests deliberately
        // document what is lost without a dictionary.)
        if (!TryGetOptionValue(args, "--dictionary", out var dictionaryPath, out var dictionaryError))
        {
            stderr.WriteLine($"Error: {dictionaryError}");
            stderr.WriteLine("--dictionary is required in --extract-layouts (without it, single-term \"(code)\" cells cannot be classified).");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return ExitArgumentError;
        }

        if (!Directory.Exists(inputDirectory))
        {
            stderr.WriteLine($"Error: directory '{inputDirectory}' does not exist.");
            return ExitArgumentError;
        }

        if (!File.Exists(dictionaryPath))
        {
            stderr.WriteLine($"Error: dictionary file '{dictionaryPath}' does not exist.");
            return ExitArgumentError;
        }

        var overwrite = HasFlag(args, "--overwrite");

        try
        {
            var summary = LayoutExtractor.Extract(inputDirectory, outputPath, overwrite, dictionaryPath);

            stdout.WriteLine($"Layout repository created in '{outputPath}'.");
            stdout.WriteLine(
                $"Files: {summary.FilesProcessed}. Table sheets (excluding TOC): {summary.SheetsProcessed}.");
            stdout.WriteLine(
                $"Declarations: {summary.DeclarationsWritten}. Ordinates: {summary.OrdinatesWritten}. " +
                $"Values: {summary.ValuesWritten}. Raw cells: {summary.RawCellsWritten}.");
            // LayoutCell is the row x column Cartesian product, kept separate from
            // LayoutOrdinate. IsShaded is always reported, even when it would be 0.
            var shadedRate = summary.CellsWritten == 0 ? 0d : (double)summary.ShadedCellsWritten / summary.CellsWritten;
            stdout.WriteLine(
                $"LayoutCell: {summary.CellsWritten} data cell(s); {summary.ShadedCellsWritten} " +
                $"shaded ({shadedRate:P1}).");
            stdout.WriteLine(
                $"Dictionary: '{summary.DictionaryPath}' (SHA-256 {summary.DictionarySha256}) - " +
                "recorded in LayoutExtraction (provenance).");

            // LayoutUnparsed is ALWAYS announced, whether it is 0 or thousands - a figure that is
            // not stated is indistinguishable from an extractor that is not looking. Broken down
            // by Kind: not everything that lands there is a defect - OrphanDeclaration includes
            // cases that are benign by design (an open axis without restriction).
            stdout.WriteLine($"LayoutUnparsed: {summary.UnparsedWritten} cell(s).");
            foreach (var (kind, count) in summary.UnparsedByKind)
            {
                stdout.WriteLine($"  {count,6}  Kind={kind}");
            }

            foreach (var (reason, count) in summary.UnparsedReasonSample)
            {
                stdout.WriteLine($"    {count,6}  {reason}");
            }

            // The third column of a row ordinate that did not have a datapoint shape: it used to
            // be left as DatapointId = null with no mark; now it is counted and reported.
            stdout.WriteLine(
                $"Row ordinates whose datapoint slot does not have that shape: {summary.OrdinateDatapointSlotMismatches} " +
                $"of {summary.OrdinatesWritten} total ordinates.");

            // X axis: a measured rate, not a promised one. Any fallback that remains is MARKED in
            // LayoutOrdinate.IsFallbackCode - never indistinguishable from a real code - and
            // characterized here per table.
            var xAxisRate = summary.XAxisOrdinatesTotal == 0 ? 0d : (double)summary.XAxisOrdinatesWithRealCode / summary.XAxisOrdinatesTotal;
            var xAxisFallback = summary.XAxisOrdinatesTotal - summary.XAxisOrdinatesWithRealCode;
            stdout.WriteLine(
                $"X axis: {summary.XAxisOrdinatesWithRealCode} of {summary.XAxisOrdinatesTotal} ordinates with a real " +
                $"DPM code ({xAxisRate:P1}); {xAxisFallback} in fallback (column letter, IsFallbackCode=1).");
            foreach (var (tableCode, count) in summary.XAxisFallbackSample)
            {
                stdout.WriteLine($"    {count,6}  {tableCode}");
            }

            // The invariant "LayoutCell.RowOrdinateId is NULL only in sheets with no Y ordinate at
            // all" holds today by the absence of a pattern, not by construction - it is ALWAYS
            // reported, even when 0, so that a future counterexample does not go unnoticed.
            stdout.WriteLine(
                "Header column cells with no row in their sheet (counterexamples of the " +
                $"LayoutCell.RowOrdinateId invariant): {summary.XAxisOrdinatesWithoutRowInSheetsThatHaveRows}.");

            // "No row ordinates" as a proxy for "open row axis" - confirmed by an IsKey
            // declaration in "Rows" as opposed to merely assumed by absence.
            stdout.WriteLine(
                $"Sheets without row ordinates: {summary.SheetsWithOpenRowAxisConfirmedByDeclaration} " +
                "confirmed by an IsKey declaration in 'Rows', " +
                $"{summary.SheetsWithOpenRowAxisAssumedByAbsenceOnly} assumed only by the absence of Y.");

            // Accounting of the value -> ordinate link (LayoutValueOrdinateLinker). The Z-axis
            // values are ALWAYS left unlinked by design (Region=Header) and count here as
            // "unlinked" - see the reason recorded in LayoutUnparsed.Reason.
            var valuesLinkable = summary.ValuesLinkedToOrdinate + summary.ValuesUnlinkedToOrdinate;
            var linkRate = valuesLinkable == 0 ? 0d : (double)summary.ValuesLinkedToOrdinate / valuesLinkable;
            stdout.WriteLine(
                $"Values linked to an ordinate: {summary.ValuesLinkedToOrdinate} of {valuesLinkable} " +
                $"({linkRate:P1}); {summary.ValuesUnlinkedToOrdinate} unlinked (LayoutUnparsed, " +
                "Kind=UnlinkedValue - the reason distinguishes a Z axis 'by design' from an extraction gap).");

            // Equality rules "==" in cell comments - a positive control of the CONVERSION that
            // does not depend on any reference export (--validate, C-EQU-01/C-EQU-02 consume it
            // from LayoutEqualityRule). ALWAYS announced, even when 0 - an extractor that never
            // opens xl/comments*.xml is indistinguishable from one that does and finds nothing.
            stdout.WriteLine(
                $"Cell comments examined: {summary.EqualityCommentsExamined}. Lines with '==': " +
                $"{summary.EqualityRuleLinesFound}. Distinct equality rules: {summary.EqualityRulesWritten} " +
                $"(LayoutEqualityRule). Grammar failures: {summary.EqualityRuleGrammarFailures} " +
                "(LayoutUnparsed, Kind=EqualityRuleGrammarFailure).");

            return ExitOk;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or DirectoryNotFoundException or Microsoft.Data.Sqlite.SqliteException)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return ExitArgumentError;
        }
    }

    private static int RunValidate(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (!TryGetOptionValue(args, "--validate", out var generatedPath, out var error))
        {
            stderr.WriteLine($"Error: {error}");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return ExitArgumentError;
        }

        var recognized = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--validate", "--reference", "--layouts", "--report",
        };
        var unknown = FindUnknownTokens(args, recognized, optionsWithValue: ["--validate", "--reference", "--layouts", "--report"]);
        if (unknown.Count > 0)
        {
            stderr.WriteLine($"Error: unrecognized argument in --validate mode: {unknown[0]}");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return ExitArgumentError;
        }

        // --reference is OPTIONAL. Without it, only plane A is run.
        string? referencePath = null;
        if (HasFlag(args, "--reference"))
        {
            if (!TryGetOptionValue(args, "--reference", out var referenceValue, out var referenceError))
            {
                stderr.WriteLine($"Error: {referenceError}");
                stderr.WriteLine();
                stderr.WriteLine(Usage);
                return ExitArgumentError;
            }

            referencePath = referenceValue;
        }

        // --layouts is OPTIONAL and INDEPENDENT of --reference.
        string? layoutsPath = null;
        if (HasFlag(args, "--layouts"))
        {
            if (!TryGetOptionValue(args, "--layouts", out var layoutsValue, out var layoutsError))
            {
                stderr.WriteLine($"Error: {layoutsError}");
                stderr.WriteLine();
                stderr.WriteLine(Usage);
                return ExitArgumentError;
            }

            layoutsPath = layoutsValue;
        }

        var reportPath = TryGetOptionValue(args, "--report", out var reportValue, out _) ? reportValue : null;

        if (!File.Exists(generatedPath))
        {
            stderr.WriteLine($"Error: file '{generatedPath}' does not exist.");
            return ExitArgumentError;
        }

        if (referencePath is not null && !File.Exists(referencePath))
        {
            stderr.WriteLine($"Error: reference file '{referencePath}' does not exist.");
            return ExitArgumentError;
        }

        if (layoutsPath is not null && !File.Exists(layoutsPath))
        {
            stderr.WriteLine($"Error: layout repository '{layoutsPath}' does not exist.");
            return ExitArgumentError;
        }

        try
        {
            var result = Validator.Run(generatedPath, referencePath, layoutsPath);
            PrintSummary(stdout, result.Report);

            if (reportPath is not null)
            {
                WriteReport(reportPath, result.Report);
            }

            return result.ExitCode;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return ExitArgumentError;
        }
    }

    private static void WriteReport(string path, EbaDpm.Converter.Core.Validation.ValidationReport report)
    {
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var json = System.Text.Json.JsonSerializer.Serialize(report, options);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void PrintSummary(TextWriter stdout, EbaDpm.Converter.Core.Validation.ValidationReport report)
    {
        var referenceText = report.Inputs.Reference is { } reference ? $"against '{Path.GetFileName(reference.Path)}'" : "without reference (plane A only)";
        stdout.WriteLine($"Validation of '{Path.GetFileName(report.Inputs.Generated.Path)}' {referenceText}.");
        stdout.WriteLine();

        if (report.Inputs.Reference is not null)
        {
            stdout.WriteLine(
                $"Comparable universe: {report.Universe.ComparableTaxonomies.Count} taxonomies, "
                + $"{report.Universe.Tables} tables, {report.Universe.Ordinates} ordinates, {report.Universe.Cells} cells.");
            stdout.WriteLine();

            // The hash of the reference file is DATA, announced ONCE here - never per exception.
            if (report.ExemplarNotice is { } notice)
            {
                stdout.WriteLine($"WARNING: {notice}");
                stdout.WriteLine();
            }
        }

        PrintPlane(stdout, report, "A", "PLANE A - internal consistency (no reference)");
        if (report.Inputs.Reference is not null)
        {
            // Plane B reports and does NOT fail the run: the reference export is another
            // implementation, not an authority.
            PrintPlane(stdout, report, "B", "PLANE B - diff against the reference (reports; does NOT decide the exit code)");
        }

        // Plane C is ALWAYS announced, whether it runs or not - never silently.
        if (report.PlaneCNotice is { } planeCNotice)
        {
            stdout.WriteLine(planeCNotice);
        }

        if (report.Checks.Any(c => c.Plane == "C"))
        {
            PrintPlane(stdout, report, "C", "PLANE C - signature by signature against the Annotated Table Layout");
        }

        stdout.WriteLine();
        var current = report.Exceptions.Count(e => e.AppliesToThisReference && !e.Stale);
        var stale = report.Exceptions.Count(e => e.Stale);
        var notEvaluated = report.Exceptions.Count(e => !e.Evaluated);
        var identifiers = report.Exceptions.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count();
        stdout.WriteLine(
            $"Exceptions: {identifiers} identifiers, {report.Exceptions.Count} objects; "
            + $"{current} current, {stale} stale, {notEvaluated} NOT EVALUATED");

        // Grouped by Id on the console - listing hundreds of lines is as useless as listing none.
        // The object-by-object detail lives ENTIRELY in the JSON (--report); only the STALE ones,
        // which require action, are named individually here.
        foreach (var group in report.Exceptions.GroupBy(e => e.Id, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var first = group.First();
            var groupEvaluated = group.Any(e => e.Evaluated);
            var status = !groupEvaluated
                ? "NOT EVALUATED"
                : group.Any(e => e.Stale)
                    ? "STALE"
                    : group.All(e => e.AppliesToThisReference)
                        ? "applied"
                        : "does not apply to this reference";
            stdout.WriteLine($"  {group.Key,-6} {group.Count(),4} object(s)  {status,-24} {first.Reason}");
        }

        // The obligatory counterpart of plane B no longer failing the run is that it be NAMED:
        // check (Scope) and declared reference of each stale exception, one by one, marking the
        // plane B ones as those that no longer fail the run.
        var staleExceptionSamples = report.Exceptions.Where(e => e.Stale).ToList();
        if (staleExceptionSamples.Count > 0)
        {
            stdout.WriteLine("  Stale, one by one:");
            foreach (var exception in staleExceptionSamples)
            {
                var gateNote = exception.Plane == "B" ? " [plane B: reports, does NOT fail the run]" : "";
                stdout.WriteLine(
                    $"    {exception.Id,-6}{exception.BusinessKey,-45} check={exception.Scope} reference={exception.Reference}{gateNote} {exception.Reason}");
            }
        }

        stdout.WriteLine();
        var criticalFailed = report.Summary.Critical.Failed;
        var informativeFailed = report.Summary.Informative.Failed;
        var staleText = report.Summary.StaleExceptions > 0 ? $" {report.Summary.StaleExceptions} stale exception(s)." : string.Empty;
        stdout.WriteLine(
            criticalFailed == 0 && report.Summary.StaleExceptions == 0
                ? $"RESULT: no critical differences. {informativeFailed} informative differences in the report."
                : $"RESULT: {criticalFailed} critical differences.{staleText} {informativeFailed} informative differences in the report.");

        // What plane B no longer fails is counted SEPARATELY, never diluted in the figures above
        // (which remain the total over the three planes).
        if (report.Summary.PlaneBCriticalFailed > 0 || report.Summary.PlaneBStaleExceptions > 0)
        {
            stdout.WriteLine(
                $"  Of the above, PLANE B (does not fail the run): {report.Summary.PlaneBCriticalFailed} failed critical check(s), "
                + $"{report.Summary.PlaneBStaleExceptions} stale exception(s).");
        }
    }

    private static void PrintPlane(TextWriter stdout, EbaDpm.Converter.Core.Validation.ValidationReport report, string plane, string header)
    {
        stdout.WriteLine(header);
        foreach (var layer in new[] { ("critical", "critical"), ("informative", "informative") })
        {
            var checks = report.Checks.Where(c => c.Plane == plane && c.Layer == layer.Item1).ToList();
            var passed = checks.Count(c => c.Status is "pass" or "skipped");
            var failed = checks.Count - passed;
            stdout.WriteLine($"  {layer.Item2,-12} {checks.Count,3} checks, {passed,3} passed, {failed,3} with differences");

            foreach (var failing in checks.Where(c => c.Status == "fail" || c.Status == "info").Take(20))
            {
                stdout.WriteLine($"    {failing.Id}: {failing.Statement} ({failing.Failed} of {failing.Examined})");
                var shown = failing.Samples.Take(10).ToList();
                foreach (var sample in shown)
                {
                    stdout.WriteLine($"      - {sample.BusinessKey}");
                }

                // Up to 10 objects per screen, then "... and N more (see report)" - N is relative
                // to the TOTAL failure of the check, not to the 50-item cut of the JSON.
                if (failing.Failed > shown.Count)
                {
                    stdout.WriteLine($"      ... and {failing.Failed - shown.Count} more (see report)");
                }
            }
        }
    }

    private static bool HasFlag(string[] args, string flag)
        => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetOptionValue(string[] args, string option, out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;

        var index = Array.FindIndex(
            args, a => string.Equals(a, option, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            error = $"missing required argument {option}.";
            return false;
        }

        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            error = $"argument {option} requires a value.";
            return false;
        }

        value = args[index + 1];
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"argument {option} cannot be empty.";
            return false;
        }

        return true;
    }

    private static List<string> FindUnknownTokens(
        string[] args, HashSet<string> recognizedFlags, string[] optionsWithValue)
    {
        var unknown = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];

            if (recognizedFlags.Contains(token))
            {
                if (optionsWithValue.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    i++; // skip the associated value
                }

                continue;
            }

            unknown.Add(token);
        }

        return unknown;
    }
}
