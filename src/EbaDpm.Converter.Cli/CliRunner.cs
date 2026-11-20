using System.Globalization;
using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Core.Mapping;
using EbaDpm.Converter.Core.Mapping.Dpm20;
using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Core.Validation;
using Microsoft.Data.Sqlite;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EbaDpm.Converter.Cli;

/// <summary>
/// Logical entry point of the CLI. The arguments are parsed by Spectre.Console.Cli into a single
/// default command (<see cref="ConverterSettings"/>); the mode is chosen by the flag present, and
/// each mode keeps the validation rules and messages it had with the hand-written parser.
///
/// Exit codes:
///   0 = success (even if there are informative differences, or plane B was skipped).
///   1 = missing or invalid arguments, or an input/output error. Also an unexpected failure,
///       which is reported in full on standard error.
///   2 = recognized mode that is not implemented yet.
///   3 = --validate: there are critical differences.
/// </summary>
public static partial class CliRunner
{
    public const int ExitOk = 0;
    public const int ExitArgumentError = 1;
    public const int ExitNotImplemented = 2;
    public const int ExitValidationCriticalDifferences = 3;

    private const string UsageHint = "Use --help to see the usage.";
    private const string ReleaseDpm10Message = "--release only applies to DPM 2.0 sources.";

    private static readonly string[] HelpFlags = ["--help", "-h", "-?", "/?"];

    /// <summary>Options that take a value, in the spelling of the command line.</summary>
    private static readonly string[] ValueOptions =
    [
        "--source", "--output", "--taxonomies", "--taxonomykeys", "--releases", "--release", "--report",
        "--validate", "--reference", "--layouts", "--extract-layouts", "--out", "--dictionary",
    ];

    private static readonly string[] ListTaxonomiesOptions = ["--list-taxonomies", "--source", "--release", "--plain"];
    private static readonly string[] SchemaOnlyOptions = ["--schema-only", "--output", "--overwrite", "--plain"];
    private static readonly string[] ExtractLayoutsOptions = ["--extract-layouts", "--out", "--dictionary", "--overwrite", "--plain"];
    private static readonly string[] ValidateOptions = ["--validate", "--reference", "--layouts", "--report", "--plain"];
    private static readonly string[] ConvertOptions =
    [
        "--source", "--output", "--taxonomies", "--taxonomykeys", "--releases", "--all", "--overwrite", "--report",
        "--verbose", "--release", "--plain",
    ];

    /// <summary>
    /// Runs the CLI over two writers. The output is always plain text (no ANSI codes, no live
    /// widgets), which is what callers that inject writers (tests, hosts) need.
    /// </summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
        => Run(args, CliConsole.Plain(stdout), CliConsole.Plain(stderr));

    /// <summary>Runs the CLI over two consoles: rich on a terminal, plain when redirected or with <c>--plain</c>.</summary>
    internal static int Run(string[] args, CliConsole stdout, CliConsole stderr)
    {
        if (args.Any(a => string.Equals(a, "--plain", StringComparison.OrdinalIgnoreCase)))
        {
            stdout = stdout.AsPlain();
            stderr = stderr.AsPlain();
        }

        // The help flags win over everything else and need no other argument.
        if (args.Any(a => HelpFlags.Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            return ShowHelp(stdout, stderr);
        }

        if (args.Length == 0)
        {
            stderr.Error("no arguments given.");
            stderr.WriteLine(UsageHint);
            stderr.WriteLine();
            ShowHelp(stderr, stderr);
            return ExitArgumentError;
        }

        if (!TryCheckValues(args, out var valueError))
        {
            return ArgumentError(stderr, valueError);
        }

        return BuildApp(stdout, new CliContext(stdout, stderr)).Run(args);
    }

    private static int ShowHelp(CliConsole target, CliConsole stderr)
    {
        var help = target.ForHelp();
        return BuildApp(help, new CliContext(help, stderr)).Run(["--help"]);
    }

    private static CommandApp<ConverterCommand> BuildApp(CliConsole console, CliContext context)
    {
        var app = new CommandApp<ConverterCommand>();
        app.Configure(config =>
        {
            config.SetApplicationName("EbaDpm.Converter");
            config.Settings.Console = console.Console;
            config.Settings.Culture = CultureInfo.InvariantCulture;
            config.Settings.CaseSensitivity = CaseSensitivity.None;
            config.Settings.StrictParsing = false;
            config.SetInterceptor(new ContextInterceptor(context));
            config.SetExceptionHandler((exception, _) => HandleException(exception, context.Stderr));

            config.AddExample("--source", "<input.accdb>", "--output", "<directory>");
            config.AddExample("--source", "<input.accdb>", "--output", "<file.db>", "--release", "<code>", "--overwrite");
            config.AddExample("--source", "<dpm1.accdb>", "--output", "<file.db>", "--releases", "4.0,4.1");
            config.AddExample("--list-taxonomies", "--source", "<input.accdb>");
            config.AddExample("--schema-only", "--output", "<file.db>");
            config.AddExample("--validate", "<generated.db>", "--layouts", "<layouts.db>", "--report", "<report.json>");
            config.AddExample("--extract-layouts", "<directory>", "--out", "<layouts.db>", "--dictionary", "<dictionary.db>");
        });

        return app;
    }

    /// <summary>
    /// Errors the parser raises are argument errors. Anything else is a failure nobody anticipated:
    /// it is reported in full (type, message and stack) instead of crashing the process, and ends
    /// with exit code 1.
    /// </summary>
    private static int HandleException(Exception exception, CliConsole stderr)
    {
        if (exception is CommandAppException)
        {
            // A stray token (one that is not an option nor the value of one) is read by the parser
            // as the name of a command; there is none, so it is an unrecognized argument.
            var stray = StrayToken().Match(exception.Message);
            return ArgumentError(stderr, stray.Success ? $"unrecognized argument: {stray.Groups[1].Value}" : exception.Message);
        }

        stderr.Error($"unexpected failure ({exception.GetType().Name}): {exception.Message}");
        stderr.WriteException(exception);
        return ExitArgumentError;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^Unknown command '(.*)'\.?$")]
    private static partial System.Text.RegularExpressions.Regex StrayToken();

    private static int ArgumentError(CliConsole stderr, string message)
    {
        stderr.Error(message);
        stderr.WriteLine(UsageHint);
        return ExitArgumentError;
    }

    /// <summary>
    /// An option that takes a value must be followed by it, and a value cannot start with
    /// <c>--</c> nor be blank.
    /// </summary>
    private static bool TryCheckValues(string[] args, out string error)
    {
        error = string.Empty;
        for (var i = 0; i < args.Length; i++)
        {
            var option = ValueOptions.FirstOrDefault(o => string.Equals(o, args[i], StringComparison.OrdinalIgnoreCase));
            if (option is null)
            {
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"argument {option} requires a value.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(args[i + 1]))
            {
                error = $"argument {option} cannot be empty.";
                return false;
            }

            i++; // skip the value
        }

        return true;
    }

    /// <summary>Entry of the default command: dispatches on the mode flag (the same precedence as ever).</summary>
    internal static int Execute(ConverterSettings settings)
    {
        var context = settings.Context!;
        var stdout = settings.Plain ? context.Stdout.AsPlain() : context.Stdout;
        var stderr = settings.Plain ? context.Stderr.AsPlain() : context.Stderr;

        if (settings.SchemaOnly)
        {
            return RunSchemaOnly(settings, stdout, stderr);
        }

        if (settings.ListTaxonomies)
        {
            return RunListTaxonomies(settings, stdout, stderr);
        }

        if (settings.ValidatePath is not null)
        {
            return RunValidate(settings, stdout, stderr);
        }

        if (settings.ExtractLayouts is not null)
        {
            return RunExtractLayouts(settings, stdout, stderr);
        }

        // Any other combination of arguments points to the full conversion mode.
        return RunConvert(settings, stdout, stderr);
    }

    /// <summary>
    /// Rejects what the selected mode does not accept: tokens that match no option at all, and
    /// options that belong to another mode. Returns the message, or null when all is accepted.
    /// </summary>
    private static string? FindUnrecognized(ConverterSettings settings, string? mode, string[] allowed)
    {
        var unknown = settings.UnknownTokens
            .Concat(settings.ProvidedOptions().Where(o => !allowed.Contains(o, StringComparer.Ordinal)))
            .FirstOrDefault();
        if (unknown is null)
        {
            return null;
        }

        return mode is null
            ? $"unrecognized argument: {unknown}"
            : $"unrecognized argument in {mode} mode: {unknown}";
    }

    private static bool TryRequire(string? value, string option, out string result, out string error)
    {
        result = value ?? string.Empty;
        error = value is null ? $"missing required argument {option}." : string.Empty;
        return value is not null;
    }

    // -------------------------------------------------------------------------------------------
    // --list-taxonomies
    // -------------------------------------------------------------------------------------------

    private static int RunListTaxonomies(ConverterSettings settings, CliConsole stdout, CliConsole stderr)
    {
        if (!TryRequire(settings.Source, "--source", out var source, out var error))
        {
            return ArgumentError(stderr, error);
        }

        if (FindUnrecognized(settings, "--list-taxonomies", ListTaxonomiesOptions) is { } unrecognized)
        {
            return ArgumentError(stderr, unrecognized);
        }

        var releaseCode = settings.Release?.Trim();

        try
        {
            // Single branching point. The detected model is always printed.
            var sourceModel = SourceModelDetector.Detect(source);

            if (sourceModel == DpmSourceModel.Dpm10)
            {
                if (releaseCode is not null)
                {
                    stderr.Error(ReleaseDpm10Message);
                    return ExitArgumentError;
                }

                stdout.WriteLine("Detected source model: DPM 1.0.");
                stdout.WriteLine();
                return RunListTaxonomiesDpm10(source, stdout);
            }

            return RunListTaxonomiesDpm20(source, releaseCode, stdout, stderr);
        }
        catch (SourceModelDetectionException ex)
        {
            stderr.Error(ex.Message);
            return ExitArgumentError;
        }
        catch (AceProviderNotFoundException ex)
        {
            stderr.Error(ex.Message);
            return ExitArgumentError;
        }
        catch (TaxonomyKeyResolutionException ex)
        {
            stderr.Error(ex.Message);
            return ExitArgumentError;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            stderr.Error(ex.Message);
            return ExitArgumentError;
        }
    }

    private static int RunListTaxonomiesDpm10(string source, CliConsole stdout)
    {
        using var reader = new Dpm10AccessReader(source);
        reader.Open();

        var taxonomies = reader.ReadTaxonomies().ToList();
        var tableCounts = reader.ReadTaxonomyTableCounts()
            .ToDictionary(c => c.TaxonomyId, c => c.TableCount);
        var taxonomyKeyByTaxonomyId = TaxonomyKeyResolver.Resolve(reader.ReadModules());

        // --list-taxonomies shows the TaxonomyKey as the main identifier.
        var table = NewTaxonomyTable(stdout);
        foreach (var taxonomy in taxonomies.OrderBy(t => t.TaxonomyCode, StringComparer.OrdinalIgnoreCase))
        {
            var tableCount = tableCounts.GetValueOrDefault(taxonomy.TaxonomyId);
            var publicationDate = taxonomy.ActualPublicationDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-";
            var taxonomyKey = taxonomyKeyByTaxonomyId.GetValueOrDefault(taxonomy.TaxonomyId, "-");
            AddTaxonomyRow(table, taxonomyKey, taxonomy.TaxonomyCode, taxonomy.DpmPackageCode, publicationDate, tableCount);
        }

        stdout.Write(table);
        stdout.WriteLine();
        stdout.WriteLine($"Total: {taxonomies.Count} taxonomies.");
        return ExitOk;
    }

    /// <summary>
    /// Reads the release catalog with a reader opened without a requested cutoff. The reader is
    /// closed before returning, so that the per-release readers are the only open ones.
    /// </summary>
    private static IReadOnlyList<Dpm20ReleaseSummary> ReadReleaseCatalog(string source)
    {
        using var reader = new Dpm20AccessReader(source);
        reader.Open();
        return Dpm20ReleaseCatalog.Build(reader);
    }

    private static string DescribeAvailableReleases(IReadOnlyList<Dpm20ReleaseSummary> catalog)
        => string.Join(
            ", ",
            catalog.Select(r => r.IsEmpty ? $"{r.Code} (no exportable module version)" : r.Code));

    /// <summary>
    /// Resolves an explicit <c>--release</c> against the catalog: an unknown code, or a release with
    /// no exportable module version, is an error.
    /// </summary>
    private static bool TryResolveRelease(
        IReadOnlyList<Dpm20ReleaseSummary> catalog, string releaseCode, out Dpm20ReleaseSummary release, out string error)
    {
        error = string.Empty;
        release = catalog.FirstOrDefault(r => string.Equals(r.Code, releaseCode, StringComparison.Ordinal))!;
        if (release is null)
        {
            error = $"unknown release '{releaseCode}'. Available releases: {DescribeAvailableReleases(catalog)}.";
            return false;
        }

        if (release.IsEmpty)
        {
            error = $"release '{releaseCode}' has no exportable module version starting in it; nothing to convert.";
            return false;
        }

        return true;
    }

    private static string SkippedReleaseMessage(Dpm20ReleaseSummary release)
        => $"Release {release.Code}: no exportable module version starts in it; skipped.";

    private static int RunListTaxonomiesDpm20(string source, string? releaseCode, CliConsole stdout, CliConsole stderr)
    {
        var catalog = ReadReleaseCatalog(source);
        stdout.WriteLine("Detected source model: DPM 2.0.");
        stdout.WriteLine();

        IReadOnlyList<Dpm20ReleaseSummary> selected = catalog;
        if (releaseCode is not null)
        {
            if (!TryResolveRelease(catalog, releaseCode, out var requested, out var error))
            {
                stderr.Error(error);
                return ExitArgumentError;
            }

            selected = [requested];
        }

        var total = 0;
        foreach (var release in selected)
        {
            if (releaseCode is null)
            {
                stdout.WriteLine(
                    $"Release {release.Code} ({release.ExportableModuleVersions} exportable module versions)",
                    CliStyles.Heading);
                if (release.IsEmpty)
                {
                    stdout.WriteLine(SkippedReleaseMessage(release), CliStyles.Informative);
                    stdout.WriteLine();
                    continue;
                }
            }

            total += ListReleaseTaxonomies(source, release, stdout);
            stdout.WriteLine();
        }

        stdout.WriteLine($"Total: {total} taxonomies.");
        return ExitOk;
    }

    /// <summary>Lists the taxonomies derived at the cutoff of one release; returns their count.</summary>
    private static int ListReleaseTaxonomies(string source, Dpm20ReleaseSummary release, CliConsole stdout)
    {
        using var reader = new Dpm20AccessReader(source, cutoffReleaseCode: release.Code);
        reader.Open();

        var derivation = Dpm20TaxonomyDeriver.Derive(
            reader.ReadReleases().ToList(),
            reader.ReadFrameworks().ToList(),
            reader.ReadModules().ToList(),
            reader.ReadModuleVersions().ToList(),
            reader.ReadModuleVersionCompositions().ToList(),
            reader.ReadTableVersions().ToList(),
            reader.CutoffReleaseId);

        var tableCounts = derivation.TableCounts.ToDictionary(c => c.TaxonomyId, c => c.TableCount);
        var taxonomies = derivation.Taxonomies
            .Where(t => string.Equals(t.DpmPackageCode, release.Code, StringComparison.Ordinal))
            .OrderBy(t => t.TaxonomyCode, StringComparer.Ordinal)
            .ToList();

        // The DPM 2.0 taxonomy is derived; it carries no publication date.
        var table = NewTaxonomyTable(stdout);
        foreach (var taxonomy in taxonomies)
        {
            var tableCount = tableCounts.GetValueOrDefault(taxonomy.TaxonomyId);
            var taxonomyKey = derivation.TaxonomyKeyByTaxonomyId.GetValueOrDefault(taxonomy.TaxonomyId, "-");
            AddTaxonomyRow(table, taxonomyKey, taxonomy.TaxonomyCode, taxonomy.DpmPackageCode, "-", tableCount);
        }

        stdout.Write(table);
        return taxonomies.Count;
    }

    private static Table NewTaxonomyTable(CliConsole stdout)
        => ReportTable.Create(
            stdout,
            ("TaxonomyKey", false), ("Code", false), ("DPM package", false), ("Published", false), ("Tables", true));

    private static void AddTaxonomyRow(
        Table table, string taxonomyKey, string taxonomyCode, string dpmPackageCode, string publicationDate, int tableCount)
        => table.AddRow(
            ReportTable.Cell(taxonomyKey),
            ReportTable.Cell(taxonomyCode),
            ReportTable.Cell(dpmPackageCode),
            ReportTable.Cell(publicationDate),
            ReportTable.Cell(tableCount));

    // -------------------------------------------------------------------------------------------
    // Convert
    // -------------------------------------------------------------------------------------------

    private static int RunConvert(ConverterSettings settings, CliConsole stdout, CliConsole stderr)
    {
        if (FindUnrecognized(settings, mode: null, ConvertOptions) is { } unrecognized)
        {
            return ArgumentError(stderr, unrecognized);
        }

        if (!TryRequire(settings.Source, "--source", out var source, out var sourceError))
        {
            return ArgumentError(stderr, sourceError);
        }

        if (!TryRequire(settings.Output, "--output", out var output, out var outputError))
        {
            return ArgumentError(stderr, outputError);
        }

        var releaseCode = settings.Release?.Trim();
        var overwrite = settings.Overwrite;
        var all = settings.All;
        var taxonomyCodes = settings.Taxonomies is { } taxonomiesValue ? SplitCsv(taxonomiesValue) : null;
        var taxonomyKeys = settings.TaxonomyKeys is { } taxonomyKeysValue ? SplitCsv(taxonomyKeysValue) : null;
        var releaseCodes = settings.Releases is { } releasesValue ? SplitCsv(releasesValue) : null;

        try
        {
            // Single branching point. The detected model is always printed.
            var sourceModel = SourceModelDetector.Detect(source);
            stdout.WriteLine(
                sourceModel == DpmSourceModel.Dpm10
                    ? "Detected source model: DPM 1.0."
                    : "Detected source model: DPM 2.0.");
            stdout.WriteLine(
                $"ACE OLEDB provider: {AceProvider.Resolve(source)} "
                + $"({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} process).");

            if (sourceModel == DpmSourceModel.Dpm20)
            {
                var selectorOption = settings.Taxonomies is not null ? "--taxonomies"
                    : settings.TaxonomyKeys is not null ? "--taxonomykeys"
                    : settings.Releases is not null ? "--releases"
                    : all ? "--all"
                    : null;
                if (selectorOption is not null)
                {
                    stderr.Error(
                        $"{selectorOption} does not apply to DPM 2.0 sources. "
                        + "Use --release <code> for one release, or no selector for one file per release.");
                    return ExitArgumentError;
                }

                return RunConvertDpm20(source, output, overwrite, releaseCode, stdout, stderr);
            }

            if (releaseCode is not null)
            {
                stderr.Error(ReleaseDpm10Message);
                return ExitArgumentError;
            }

            ConvertDpm10(source, output, overwrite, new TaxonomySelectionRequestInput(taxonomyCodes, taxonomyKeys, releaseCodes, all), stdout);
            return ExitOk;
        }
        catch (SourceModelDetectionException ex)
        {
            stderr.Error(ex.Message);
            return ExitArgumentError;
        }
        catch (AceProviderNotFoundException ex)
        {
            stderr.Error(ex.Message);
            return ExitArgumentError;
        }
        catch (TaxonomySelectionException ex)
        {
            stderr.Error(ex.Message);
            return ExitArgumentError;
        }
        catch (TaxonomyKeyResolutionException ex)
        {
            stderr.Error(ex.Message);
            return ExitArgumentError;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            stderr.Error(ex.Message);
            return ExitArgumentError;
        }
    }

    private sealed record TaxonomySelectionRequestInput(
        List<string>? TaxonomyCodes, List<string>? TaxonomyKeys, List<string>? ReleaseCodes, bool All);

    private static readonly string[] Dpm10Phases =
    [
        "Create schema", "Read source", "Skeleton", "Dictionary", "Templates and tables", "Axes and cells", "Modules", "VACUUM",
    ];

    /// <summary>
    /// DPM 1.0 conversion into one file. The schema is created before the source is read, as it
    /// always was. The phases are reported while they run; the summary is written at the end.
    /// </summary>
    private static void ConvertDpm10(
        string source, string output, bool overwrite, TaxonomySelectionRequestInput selection, CliConsole stdout)
    {
        var selectedCount = 0;
        SkeletonLoader.Result skeleton = null!;
        DictionaryLoader.Result dictionary = null!;
        TemplateOrTableLoader.Result templates = null!;
        AxisAndCellLoader.Result axesAndCells = null!;
        ModuleLoader.Result modules = null!;
        var vacuumElapsedMilliseconds = 0L;

        Phases.Run(stdout, Dpm10Phases, phases =>
        {
            using var destination = phases.Run("Create schema", () =>
            {
                SchemaCreator.Create(output, overwrite);
                return OpenDestination(output);
            });

            using var accessReader = new Dpm10AccessReader(source);
            var read = phases.Run("Read source", () =>
            {
                accessReader.Open();

                var allTaxonomies = accessReader.ReadTaxonomies().ToList();
                var taxonomyKeyByTaxonomyId = TaxonomyKeyResolver.Resolve(accessReader.ReadModules());
                var request = new TaxonomySelectionRequest(
                    selection.TaxonomyCodes, selection.TaxonomyKeys, selection.ReleaseCodes, selection.All);
                var selectedTaxonomies = TaxonomySelector.Resolve(allTaxonomies, request, taxonomyKeyByTaxonomyId);
                return (AllTaxonomies: allTaxonomies, SelectedTaxonomies: selectedTaxonomies);
            });
            selectedCount = read.SelectedTaxonomies.Count;

            skeleton = phases.Run("Skeleton", () => SkeletonLoader.Load(accessReader, destination, read.AllTaxonomies, read.SelectedTaxonomies));
            dictionary = phases.Run("Dictionary", () => DictionaryLoader.Load(accessReader, destination, read.AllTaxonomies, read.SelectedTaxonomies));
            templates = phases.Run("Templates and tables", () => TemplateOrTableLoader.Load(accessReader, destination, read.SelectedTaxonomies));
            axesAndCells = phases.Run(
                "Axes and cells",
                () => AxisAndCellLoader.Load(accessReader, destination, templates.TableIdByTableVId, dictionary.MemberIdRenumbering));
            modules = phases.Run("Modules", () => ModuleLoader.Load(accessReader, destination, read.SelectedTaxonomies));
            vacuumElapsedMilliseconds = phases.Run("VACUUM", () => VacuumWithTiming(destination));
        });

        stdout.WriteLine();
        stdout.WriteLine(
            $"Converted into '{output}': {selectedCount} taxonomies, floor release {skeleton.FloorReleaseCode}.",
            CliStyles.Heading);

        var summary = new ConversionSummary(stdout);
        summary.Add("Skeleton", "frameworks", skeleton.ReportingFrameworkRows);
        summary.Add("Skeleton", "releases", skeleton.ReleaseRows, $"floor {skeleton.FloorReleaseCode}");
        summary.Add("Skeleton", "concepts", skeleton.ConceptRows);
        summary.Add("Dictionary", "domains", dictionary.DomainRows);
        summary.Add("Dictionary", "members", dictionary.MemberRows);
        summary.Add("Dictionary", "dimensions", dictionary.DimensionRows);
        summary.Add("Dictionary", "metrics", dictionary.MetricRows);
        summary.Add("Dictionary", "hierarchies", dictionary.HierarchyRows);
        summary.Add("Dictionary", "hierarchy nodes", dictionary.HierarchyNodeRows);
        summary.Add("Dictionary", "new concepts", dictionary.NewConceptRows);
        summary.Add("Dictionary", "concept translations", dictionary.ConceptTranslationRows);
        summary.Add("Templates and tables", "template/table nodes", templates.TemplateOrTableRows);
        summary.Add("Templates and tables", "tables", templates.TableRows);
        summary.Add("Templates and tables", "taxonomy-table links", templates.TaxonomyTableRows);
        summary.Add("Axes and cells", "axes", axesAndCells.AxisRows);
        summary.Add("Axes and cells", "ordinates", axesAndCells.AxisOrdinateRows);
        summary.Add("Axes and cells", "categorisations", axesAndCells.OrdinateCategorisationRows);
        summary.Add("Axes and cells", "cells", axesAndCells.TableCellRows);
        summary.Add("Axes and cells", "positions", axesAndCells.CellPositionRows);
        summary.Add("Axes and cells", "open-axis restrictions", axesAndCells.OpenAxisValueRestrictionRows);
        summary.Add("Axes and cells", "metric restrictions", axesAndCells.OrdinateMetricRestrictionRows);
        summary.Add("Axes and cells", "resets skipped", axesAndCells.SkippedDefaultMemberResets);
        summary.Add(
            "Axes and cells", "cells with a metric inherited from an ancestor", axesAndCells.MetricInheritedFromAncestorCellCount,
            "expected 0", axesAndCells.MetricInheritedFromAncestorCellCount == 0 ? CliStyles.Plain : CliStyles.Informative);
        summary.Add(
            "Sentinel categorisations", "fixed categorisations transferred", axesAndCells.TransferredSentinelFixedPairs,
            "fixed categorisations of the sentinel moved to their target ordinate (1:1, or 1:2 on a single dimension)");
        summary.Add(
            "Sentinel categorisations", "sentinels left undecided", axesAndCells.AmbiguousSentinelFixedPairSkips,
            "partition shape other than those two",
            axesAndCells.AmbiguousSentinelFixedPairSkips == 0 ? CliStyles.Plain : CliStyles.Informative);
        summary.Add("Modules", "modules", modules.ModuleRows);
        summary.Add("Modules", "conceptual modules", modules.ConceptualModuleRows);
        summary.Add("Modules", "module-template links", modules.ModuleBusinessTemplateRows);
        summary.AddDiagnostics(
            "Undecided sentinel categorisation", axesAndCells.AmbiguousSentinelFixedPairDetails);
        summary.Write();

        stdout.WriteLine($"Final VACUUM: {vacuumElapsedMilliseconds} ms.");
    }

    /// <summary>
    /// DPM 2.0 branch of <see cref="RunConvert"/>. With <paramref name="releaseCode"/>, converts that
    /// release into the file <paramref name="output"/>; without it, <paramref name="output"/> is a
    /// directory and one file <c>dpm-&lt;release code&gt;.db</c> is written per non-empty release.
    /// </summary>
    private static int RunConvertDpm20(
        string source, string output, bool overwrite, string? releaseCode, CliConsole stdout, CliConsole stderr)
    {
        var catalog = ReadReleaseCatalog(source);

        if (releaseCode is not null)
        {
            if (!TryResolveRelease(catalog, releaseCode, out var release, out var error))
            {
                stderr.Error(error);
                return ExitArgumentError;
            }

            if (Directory.Exists(output))
            {
                stderr.Error($"--output '{output}' is a directory; with --release it must be the path of the file to write.");
                return ExitArgumentError;
            }

            return ConvertDpm20Release(source, release.Code, output, overwrite, stdout);
        }

        if (File.Exists(output))
        {
            stderr.Error($"--output '{output}' is a file; without --release it must be a directory.");
            return ExitArgumentError;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var targets = new List<(Dpm20ReleaseSummary Release, string Path)>();
        foreach (var release in catalog)
        {
            if (release.IsEmpty)
            {
                stdout.WriteLine(SkippedReleaseMessage(release), CliStyles.Informative);
                continue;
            }

            var safeCode = new string(release.Code.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            targets.Add((release, Path.Join(output, $"dpm-{safeCode}.db")));
        }

        if (targets.Count == 0)
        {
            stderr.Error("no release has an exportable module version; nothing to convert.");
            return ExitArgumentError;
        }

        var existing = targets.Where(t => File.Exists(t.Path)).Select(t => t.Path).ToList();
        if (existing.Count > 0 && !overwrite)
        {
            stderr.Error("the following output files already exist (use --overwrite to replace them); nothing was written:");
            foreach (var path in existing)
            {
                stderr.WriteLine($"  {path}");
            }

            return ExitArgumentError;
        }

        Directory.CreateDirectory(output);
        foreach (var (release, path) in targets)
        {
            stdout.WriteLine();
            stdout.WriteLine(
                $"=== Release {release.Code} ({release.ExportableModuleVersions} exportable module versions) -> '{path}' ===",
                CliStyles.Heading);
            var exit = ConvertDpm20Release(source, release.Code, path, overwrite, stdout);
            if (exit != ExitOk)
            {
                return exit;
            }
        }

        return ExitOk;
    }

    private static readonly string[] Dpm20Phases =
    [
        "Read source", "Create schema", "Skeleton", "Dictionary", "Hierarchy tree", "Structure", "Axes and cells",
        "Modules", "Concepts", "VACUUM",
    ];

    /// <summary>
    /// Creates, loads and finalizes the SQLite snapshot of one DPM 2.0 release: the skeleton
    /// (<see cref="Dpm20SkeletonLoader"/>), the full dictionary
    /// (<see cref="Dpm20DictionaryLoader"/>), the hierarchy tree
    /// (<see cref="Dpm20HierarchyNodeLoader"/>: <c>mHierarchyNode</c>), the structure
    /// (<see cref="Dpm20StructureLoader"/>), the axes/cells/signatures
    /// (<see cref="Dpm20AxisAndCellLoader"/>), the modules (<see cref="Dpm20ModuleLoader"/>:
    /// <c>mModule</c>, <c>mConceptualModule</c>, <c>mModuleBusinessTemplate</c>) and, in a FINAL
    /// phase of its own after all the previous loaders, the concepts
    /// (<see cref="Dpm20ConceptLoader"/>: <c>mConcept</c>, <c>mConceptTranslation</c>).
    /// The reader's cutoff is the release itself.
    /// </summary>
    private static int ConvertDpm20Release(string source, string releaseCode, string output, bool overwrite, CliConsole stdout)
    {
        using var accessReader = new Dpm20AccessReader(source, cutoffReleaseCode: releaseCode);
        accessReader.Open();

        stdout.WriteLine($"Release: {accessReader.CutoffReleaseCode}.");

        Dpm20SkeletonLoader.Result skeleton = null!;
        Dpm20DictionaryLoader.Result dictionary = null!;
        Dpm20DictionaryLoader.Diagnostics dictionaryDiagnostics = null!;
        Dpm20HierarchyNodeLoader.Result hierarchyNodes = null!;
        Dpm20StructureLoader.Result structure = null!;
        Dpm20AxisAndCellLoader.Result axesAndCells = null!;
        Dpm20ModuleLoader.Result moduleResult = null!;
        Dpm20ConceptLoader.Result concepts = null!;
        var vacuumElapsedMilliseconds = 0L;

        Phases.Run(stdout, Dpm20Phases, phases =>
        {
            var read = phases.Run("Read source", () =>
            {
                var releases = accessReader.ReadReleases().ToList();
                var frameworks = accessReader.ReadFrameworks().ToList();
                var modules = accessReader.ReadModules().ToList();
                var moduleVersions = accessReader.ReadModuleVersions().ToList();
                var compositions = accessReader.ReadModuleVersionCompositions().ToList();
                var tableVersions = accessReader.ReadTableVersions().ToList();
                var moduleVersionHistory = accessReader.ReadModuleVersionHistory().ToList();

                var derivation = Dpm20TaxonomyDeriver.Derive(
                    releases, frameworks, modules, moduleVersions, compositions, tableVersions, accessReader.CutoffReleaseId);

                var request = new TaxonomySelectionRequest(null, null, [accessReader.CutoffReleaseCode], false);
                var selectedTaxonomies = TaxonomySelector.Resolve(derivation.Taxonomies, request, derivation.TaxonomyKeyByTaxonomyId);
                return (releases, frameworks, modules, moduleVersions, compositions, tableVersions, moduleVersionHistory, derivation, selectedTaxonomies);
            });

            using var destination = phases.Run("Create schema", () =>
            {
                SchemaCreator.Create(output, overwrite);
                return OpenDestination(output);
            });

            skeleton = phases.Run(
                "Skeleton",
                () => Dpm20SkeletonLoader.Load(
                    read.releases, read.frameworks, read.selectedTaxonomies, accessReader.CutoffReleaseCode,
                    accessReader.CutoffReleaseId, destination, read.moduleVersionHistory));

            dictionary = phases.Run(
                "Dictionary",
                () =>
                {
                    var result = Dpm20DictionaryLoader.Load(accessReader, destination, out var diagnostics);
                    dictionaryDiagnostics = diagnostics;
                    return result;
                });

            hierarchyNodes = phases.Run(
                "Hierarchy tree",
                () => Dpm20HierarchyNodeLoader.Load(accessReader, destination, dictionary.HierarchyBySubCategoryId));

            structure = phases.Run(
                "Structure",
                () => Dpm20StructureLoader.Load(accessReader, destination, read.derivation, read.tableVersions, read.selectedTaxonomies));

            axesAndCells = phases.Run(
                "Axes and cells",
                () => Dpm20AxisAndCellLoader.Load(
                    accessReader, destination, structure.TableVIdByTableId, read.tableVersions, dictionary.HierarchyBySubCategoryId));

            moduleResult = phases.Run(
                "Modules",
                () => Dpm20ModuleLoader.Load(
                    accessReader, destination, read.modules, read.moduleVersions, read.compositions, read.tableVersions,
                    read.frameworks, read.releases, read.selectedTaxonomies, structure));

            // FINAL phase, after the six previous loaders. Mints mConcept and mConceptTranslation by
            // reading what has already been written; it does not touch any of the six loaders above.
            concepts = phases.Run("Concepts", () => Dpm20ConceptLoader.Load(destination));

            vacuumElapsedMilliseconds = phases.Run("VACUUM", () => VacuumWithTiming(destination));
        });

        stdout.WriteLine();
        stdout.WriteLine($"DPM 2.0 release {accessReader.CutoffReleaseCode} converted into '{output}'.", CliStyles.Heading);

        var summary = new ConversionSummary(stdout);
        summary.Add("Skeleton", "mRelease", skeleton.ReleaseRows);
        summary.Add("Skeleton", "mReportingFramework", skeleton.ReportingFrameworkRows);
        summary.Add("Skeleton", "mTaxonomy", skeleton.TaxonomyRows);
        summary.Add("Skeleton", "mOwner", skeleton.OwnerRows);
        summary.Add("Skeleton", "owner-parent", skeleton.OwnerParentRows);
        summary.Add("Skeleton", "languages", skeleton.LanguageRows);
        summary.Add("Skeleton", "rewrite-URI", skeleton.RewriteUriRows);
        summary.Add("Skeleton", "taxonomy package", skeleton.TaxonomyPackageRows);
        summary.Add("Skeleton", "database properties", skeleton.DatabasePropertiesRows);
        summary.Add("Dictionary", "mDomain", dictionary.DomainRows);
        summary.Add("Dictionary", "domain unions", dictionary.DomainUnionRows);
        summary.Add("Dictionary", "mMember", dictionary.MemberRows);
        summary.Add("Dictionary", "mMetric", dictionary.MetricRows);
        summary.Add("Dictionary", "mDimension", dictionary.DimensionRows);
        summary.Add("Dictionary", "mHierarchy", dictionary.HierarchyRows);
        summary.Add("Hierarchy tree", "mHierarchyNode", hierarchyNodes.HierarchyNodeRows);
        summary.Add(
            "Hierarchy tree", "hierarchies without nodes", hierarchyNodes.HierarchiesWithoutNodes,
            "no items in their effective version");
        summary.Add("Structure", "mTable", structure.TableRows);
        summary.Add("Structure", "mTaxonomyTable", structure.TaxonomyTableRows);
        summary.Add(
            "Structure", "mTemplateOrTable", structure.TemplateOrTableRows,
            $"{structure.TableGroupLevel1Rows} group + {structure.TemplateLevel2Rows} template + {structure.BusinessTableRows} table");
        summary.AddStatement(
            "Structure", "invariant mTaxonomyTable == BusinessTable",
            structure.PairInvariantHolds ? "holds" : "DOES NOT HOLD",
            structure.PairInvariantHolds ? CliStyles.Pass : CliStyles.Critical);
        summary.Add("Axes and cells", "mAxis", axesAndCells.AxisRows);
        summary.Add("Axes and cells", "mTableAxis", axesAndCells.TableAxisRows);
        summary.Add("Axes and cells", "mAxisOrdinate", axesAndCells.AxisOrdinateRows);
        summary.Add("Axes and cells", "mTableCell", axesAndCells.TableCellRows);
        summary.Add("Axes and cells", "mCellPosition", axesAndCells.CellPositionRows);
        summary.Add(
            "Axes and cells", "mOrdinateCategorisation", axesAndCells.OrdinateCategorisationRows,
            "informative, not compared by count");
        summary.Add("Axes and cells", "mOpenAxisValueRestriction", axesAndCells.OpenAxisValueRestrictionRows);
        summary.Add(
            "Axes and cells", "mOrdinateMetricRestriction", axesAndCells.OrdinateMetricRestrictionRows,
            $"{axesAndCells.UnresolvedMetricRestrictions} unresolved",
            axesAndCells.UnresolvedMetricRestrictions == 0 ? CliStyles.Plain : CliStyles.Informative);
        AddUnresolved(summary, "unresolved context pairs", axesAndCells.UnresolvedContextPairs);
        AddUnresolved(summary, "unresolved metric pairs", axesAndCells.UnresolvedMetricPairs);
        AddUnresolved(summary, "unresolved open-axis dimensions", axesAndCells.UnresolvedOpenAxisDimensions);
        AddUnresolved(summary, "unresolved open-axis restrictions", axesAndCells.UnresolvedOpenAxisRestrictions);
        summary.Add("Modules", "mModule", moduleResult.ModuleRows);
        summary.Add("Modules", "mConceptualModule", moduleResult.ConceptualModuleRows);
        summary.Add(
            "Modules", "mModuleBusinessTemplate", moduleResult.ModuleBusinessTemplateRows,
            "Order: a declared choice, not derived");
        summary.Add("Concepts", "mConcept", concepts.ConceptRows, $"{concepts.ConceptsWithReleaseId} with ReleaseID");
        summary.Add("Concepts", "mConceptTranslation", concepts.ConceptTranslationRows);
        foreach (var type in concepts.ConceptRowsByType.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            summary.Add("Concepts", $"  {type.Key}", type.Value);
        }

        summary.AddDiagnostics("mTaxonomy: unresolved label/version/date columns", skeleton.UnresolvedTaxonomyColumns ?? []);
        AddDictionaryDiagnostics(summary, dictionaryDiagnostics);
        summary.AddDiagnostics("Hierarchy tree: MemberID anchored to its own domain", hierarchyNodes.AnchoredMembers);
        summary.AddDiagnostics("Hierarchy tree: MemberID re-anchored by the consistency tie-break", hierarchyNodes.ReanchoredMembers);
        summary.AddDiagnostics("Structure: unresolved cases", structure.UnresolvedCases);
        summary.AddDiagnostics("Axes and cells: structural anomalies", axesAndCells.StructuralAnomalies);
        summary.AddDiagnostics("Modules: unresolved cases", moduleResult.UnresolvedCases);
        summary.Write();

        stdout.WriteLine($"Final VACUUM: {vacuumElapsedMilliseconds} ms.");
        return ExitOk;
    }

    private static void AddUnresolved(ConversionSummary summary, string item, long count)
        => summary.Add("Axes and cells", item, count, null, count == 0 ? CliStyles.Plain : CliStyles.Informative);

    /// <summary>
    /// The named cases that are documented as not fully resolved: they are listed, not hidden and
    /// not forced.
    /// </summary>
    private static void AddDictionaryDiagnostics(
        ConversionSummary summary, Dpm20DictionaryLoader.Diagnostics diagnostics)
    {
        summary.AddDiagnostics("mMetric.DataType without a known translation", diagnostics.UnresolvedMetricDataTypeCases);
        summary.AddDiagnostics("mMetric with several hierarchies in its non-key headers (mMetric.ReferencedHierarchyID NULL; the hierarchy is per ordinate in mOrdinateMetricRestriction)", diagnostics.MetricsWithSeveralHierarchies);
        summary.AddDiagnostics("mDomain.DataType unresolved", diagnostics.UnresolvedDomainDataTypeCases);
        summary.AddDiagnostics("mDimension without its own code in ItemCategory/_PR", diagnostics.DimensionsWithoutCode);
        summary.AddDiagnostics("mDimension without a row in PropertyCategory", diagnostics.DimensionsWithoutPropertyCategory);
        summary.AddDiagnostics("mDimension.DomainID unresolved", diagnostics.UnresolvedDimensionDomainCases);
    }

    /// <summary>
    /// Opens the destination SQLite database that <see cref="SchemaCreator.Create"/> has just
    /// created. The schema is created by the caller, before it reads the source.
    /// </summary>
    private static SqliteConnection OpenDestination(string output)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = output,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString();

        var destination = new SqliteConnection(connectionString);
        destination.Open();
        return destination;
    }

    /// <summary>Runs the final VACUUM and returns the elapsed milliseconds.</summary>
    private static long VacuumWithTiming(SqliteConnection destination)
    {
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
        return vacuumStopwatch.ElapsedMilliseconds;
    }

    private static List<string> SplitCsv(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // -------------------------------------------------------------------------------------------
    // --schema-only
    // -------------------------------------------------------------------------------------------

    private static int RunSchemaOnly(ConverterSettings settings, CliConsole stdout, CliConsole stderr)
    {
        if (!TryRequire(settings.Output, "--output", out var output, out var error))
        {
            return ArgumentError(stderr, error);
        }

        if (FindUnrecognized(settings, "--schema-only", SchemaOnlyOptions) is { } unrecognized)
        {
            return ArgumentError(stderr, unrecognized);
        }

        try
        {
            var tableCount = SchemaCreator.Create(output, settings.Overwrite);
            stdout.WriteLine($"Schema created in '{output}': {tableCount} tables.");
            return ExitOk;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            stderr.Error(ex.Message);
            return ExitArgumentError;
        }
    }

    // -------------------------------------------------------------------------------------------
    // --extract-layouts
    // -------------------------------------------------------------------------------------------

    private static int RunExtractLayouts(ConverterSettings settings, CliConsole stdout, CliConsole stderr)
    {
        var inputDirectory = settings.ExtractLayouts!;

        if (!TryRequire(settings.Out, "--out", out var outputPath, out var outputError))
        {
            return ArgumentError(stderr, outputError);
        }

        if (FindUnrecognized(settings, "--extract-layouts", ExtractLayoutsOptions) is { } unrecognized)
        {
            return ArgumentError(stderr, unrecognized);
        }

        // --dictionary is REQUIRED on the CLI. Without it a single-term "(code)" cannot be
        // classified (metric vs key dimension) nor can "(domain:member)" be validated - and a
        // fallback that mislabels hundreds of cells is worse than refusing. (The library,
        // LayoutExtractor.Extract, keeps dictionaryPath optional: several tests deliberately
        // document what is lost without a dictionary.)
        if (!TryRequire(settings.Dictionary, "--dictionary", out var dictionaryPath, out var dictionaryError))
        {
            stderr.Error(dictionaryError);
            stderr.WriteLine("--dictionary is required in --extract-layouts (without it, single-term \"(code)\" cells cannot be classified).");
            stderr.WriteLine(UsageHint);
            return ExitArgumentError;
        }

        if (!Directory.Exists(inputDirectory))
        {
            stderr.Error($"directory '{inputDirectory}' does not exist.");
            return ExitArgumentError;
        }

        if (!File.Exists(dictionaryPath))
        {
            stderr.Error($"dictionary file '{dictionaryPath}' does not exist.");
            return ExitArgumentError;
        }

        try
        {
            LayoutExtractionSummary summary = null!;
            Phases.Run(stdout, ["Extract layouts"], phases =>
                summary = phases.Run(
                    "Extract layouts",
                    () => LayoutExtractor.Extract(inputDirectory, outputPath, settings.Overwrite, dictionaryPath)));

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
            stderr.Error(ex.Message);
            return ExitArgumentError;
        }
    }

    // -------------------------------------------------------------------------------------------
    // --validate
    // -------------------------------------------------------------------------------------------

    private static int RunValidate(ConverterSettings settings, CliConsole stdout, CliConsole stderr)
    {
        var generatedPath = settings.ValidatePath!;

        if (FindUnrecognized(settings, "--validate", ValidateOptions) is { } unrecognized)
        {
            return ArgumentError(stderr, unrecognized);
        }

        // --reference is OPTIONAL. Without it, only plane A is run.
        var referencePath = settings.Reference;

        // --layouts is OPTIONAL and INDEPENDENT of --reference.
        var layoutsPath = settings.Layouts;

        var reportPath = settings.Report;

        if (!File.Exists(generatedPath))
        {
            stderr.Error($"file '{generatedPath}' does not exist.");
            return ExitArgumentError;
        }

        if (referencePath is not null && !File.Exists(referencePath))
        {
            stderr.Error($"reference file '{referencePath}' does not exist.");
            return ExitArgumentError;
        }

        if (layoutsPath is not null && !File.Exists(layoutsPath))
        {
            stderr.Error($"layout repository '{layoutsPath}' does not exist.");
            return ExitArgumentError;
        }

        try
        {
            Validator.Result result = null!;
            Phases.Run(stdout, ["Validate"], phases =>
                result = phases.Run("Validate", () => Validator.Run(generatedPath, referencePath, layoutsPath)));
            ValidationConsoleReport.Print(stdout, result.Report);

            if (reportPath is not null)
            {
                WriteReport(reportPath, result.Report);
            }

            return result.ExitCode;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            stderr.Error(ex.Message);
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
}
