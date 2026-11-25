using System.Globalization;
using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Native.Catalog;
using EbaDpm.Converter.Native.Conversion;
using EbaDpm.Converter.Native.Schema;
using Spectre.Console;

namespace EbaDpm.Converter.Cli;

/// <summary>The output schema requested with <c>--target</c>.</summary>
internal enum OutputTarget
{
    /// <summary>The 45-table DPM distribution schema (the default).</summary>
    Distribution,

    /// <summary>The native DPM 2.0 output: the Access database table for table.</summary>
    Native,
}

/// <summary>The native output: <c>--target native</c> for the conversion and <c>--schema-only</c>.</summary>
public static partial class CliRunner
{
    private static readonly string[] SelectorOptions = ["--taxonomies", "--taxonomykeys", "--releases", "--all"];

    /// <summary>Parses <c>--target</c>: exactly <c>distribution</c> or <c>native</c> (lower case); absent means distribution.</summary>
    private static bool TryParseTarget(string? value, out OutputTarget target, out string error)
    {
        error = string.Empty;
        target = OutputTarget.Distribution;
        switch (value)
        {
            case null:
            case "distribution":
                return true;
            case "native":
                target = OutputTarget.Native;
                return true;
            default:
                error = $"invalid value '{value}' for --target. Valid values: distribution, native.";
                return false;
        }
    }

    /// <summary>
    /// What the native output does not take: the release (one file holds every release) and the
    /// DPM 1.0 selectors. Returns the message, or null when none was given.
    /// </summary>
    private static string? FindNativeInapplicable(ConverterSettings settings)
    {
        if (settings.Release is not null)
        {
            return "--release does not apply to --target native: the native output holds every release of the "
                + "source in one file.";
        }

        var selector = SelectorOptions.FirstOrDefault(option => settings.ProvidedOptions().Contains(option, StringComparer.Ordinal));
        return selector is null
            ? null
            : $"{selector} does not apply to --target native: the native output copies the whole DPM 2.0 database, "
                + "with no taxonomy selection.";
    }

    private static int RunSchemaOnlyNative(ConverterSettings settings, string output, CliConsole stdout, CliConsole stderr)
    {
        try
        {
            var ddl = NativeSchema.CreateEmpty(output, settings.Overwrite);
            stdout.WriteLine(
                $"Native schema created in '{output}': {ddl.Tables.Count} tables, {ddl.IndexStatements.Count} indexes "
                + $"(baseline release {NativeBaseline.Load().ReleaseCode}).");
            return ExitOk;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            stderr.Error(ex.Message);
            return ExitArgumentError;
        }
    }

    private static int RunConvertNative(ConverterSettings settings, CliConsole stdout, CliConsole stderr)
    {
        if (FindNativeInapplicable(settings) is { } inapplicable)
        {
            return ArgumentError(stderr, inapplicable);
        }

        if (!TryRequire(settings.Source, "--source", out var source, out var sourceError))
        {
            return ArgumentError(stderr, sourceError);
        }

        if (!TryRequire(settings.Output, "--output", out var output, out var outputError))
        {
            return ArgumentError(stderr, outputError);
        }

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

            if (sourceModel == DpmSourceModel.Dpm10)
            {
                stderr.Error("--target native applies only to DPM 2.0 sources: the native output exists only for DPM 2.0.");
                return ExitArgumentError;
            }

            if (Directory.Exists(output))
            {
                stderr.Error($"--output '{output}' is a directory; with --target native it must be the path of the file to write.");
                return ExitArgumentError;
            }

            var summary = NativeConsoleProgress.Run(
                stdout, progress => NativeConverter.Convert(source, output, settings.Overwrite, progress));
            WriteNativeSummary(stdout, summary);
            return ExitOk;
        }
        catch (Exception ex) when (ex is NativeSourceNotDpm20Exception or NativeSourceBelowBaselineException)
        {
            stderr.Error(ex.Message);
            return ExitArgumentError;
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
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException
                                       or UnauthorizedAccessException or NotSupportedException)
        {
            stderr.Error(ex.Message);
            return ExitArgumentError;
        }
    }

    private static void WriteNativeSummary(CliConsole stdout, NativeConversionSummary summary)
    {
        var baseline = NativeBaseline.Load();
        stdout.WriteLine();
        stdout.WriteLine(
            $"Native DPM 2.0 output written to '{summary.OutputPath}': {summary.Tables.Count} tables, "
            + $"{Format(summary.TotalRows)} rows (source current release {summary.CurrentReleaseCode}).",
            CliStyles.Heading);

        var table = ReportTable.Create(stdout, ("Table", false), ("Columns", true), ("Rows", true), ("Time", true));
        foreach (var result in summary.Tables)
        {
            table.AddRow(
                ReportTable.Cell(result.Name),
                ReportTable.Cell(result.Columns),
                ReportTable.Cell(result.Rows),
                ReportTable.Cell(Phases.FormatElapsed(result.Elapsed)));
        }

        stdout.Write(table);

        var schema = summary.Schema;
        stdout.WriteLine();
        stdout.WriteLine($"Tables: {summary.Tables.Count}. Rows: {Format(summary.TotalRows)}.");
        stdout.WriteLine(
            $"Excluded tables present in the source: {summary.ExcludedTablesInSource.Count}"
            + (summary.ExcludedTablesInSource.Count > 0 ? $" ({string.Join(", ", summary.ExcludedTablesInSource)})." : "."));
        stdout.WriteLine(
            $"Foreign keys declared: {schema.ForeignKeysDeclared.Count}; not declared (a table is excluded): "
            + $"{schema.ForeignKeysDropped.Count}. Indexes: {schema.IndexStatements.Count}.");

        var differences = summary.Drift;
        if (differences.Count == 0)
        {
            stdout.WriteLine($"Schema: identical to the {baseline.ReleaseCode} baseline.");
        }
        else
        {
            stdout.WriteLine(
                $"Schema drift against the {baseline.ReleaseCode} baseline: {differences.Count} difference(s) "
                + "(informative: it does not fail the conversion).",
                CliStyles.Informative);
            foreach (var difference in differences)
            {
                stdout.WriteLine($"  - {difference.Kind} {difference.Subject}: {difference.Baseline ?? "-"} -> {difference.Actual ?? "-"}");
            }
        }

        stdout.WriteLine(
            $"Time: {Phases.FormatElapsed(summary.Elapsed)}. "
            + $"Size: {(summary.FileSizeBytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)} MB.");
    }

    private static string Format(long number) => number.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>
/// Progress of the native conversion: a live list of its stages (the table copy as a counter with the
/// table being copied) on a terminal, one line per finished stage and per copied table otherwise.
/// </summary>
internal static class NativeConsoleProgress
{
    private static readonly (NativeConversionStage Stage, string Name)[] Stages =
    [
        (NativeConversionStage.ReadCatalogue, "Read catalogue"),
        (NativeConversionStage.CreateSchema, "Create schema"),
        (NativeConversionStage.CopyTables, "Copy tables"),
        (NativeConversionStage.CreateIndexes, "Create indexes"),
        (NativeConversionStage.Vacuum, "VACUUM"),
    ];

    public static NativeConversionSummary Run(
        CliConsole console, Func<Action<NativeConversionEvent>, NativeConversionSummary> work)
    {
        var names = Stages.ToDictionary(s => s.Stage, s => s.Name);

        if (!console.LiveWidgets)
        {
            return work(e => WritePlain(console, names, e));
        }

        NativeConversionSummary summary = null!;
        console.Console
            .Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ElapsedTimeColumn(),
                new SpinnerColumn { CompletedText = "done" })
            .Start(context =>
            {
                var tasks = Stages.ToDictionary(
                    s => s.Stage,
                    s => context.AddTask(s.Name, autoStart: false, maxValue: 1));
                summary = work(e =>
                {
                    var task = tasks[e.Stage];
                    switch (e.Kind)
                    {
                        case NativeConversionEventKind.StageStarted:
                            if (e.Stage == NativeConversionStage.CopyTables)
                            {
                                task.MaxValue = Math.Max(1, e.TableCount);
                            }
                            else
                            {
                                task.IsIndeterminate = true;
                            }

                            task.StartTask();
                            break;
                        case NativeConversionEventKind.TableCopied:
                            task.Value = e.TableNumber;
                            task.Description = $"Copy tables ({e.TableNumber}/{e.TableCount}): {e.Table}";
                            break;
                        case NativeConversionEventKind.StageFinished:
                            task.IsIndeterminate = false;
                            task.Value = task.MaxValue;
                            task.StopTask();
                            break;
                    }
                });
            });
        return summary;
    }

    private static void WritePlain(CliConsole console, Dictionary<NativeConversionStage, string> names, NativeConversionEvent e)
    {
        switch (e.Kind)
        {
            case NativeConversionEventKind.TableCopied:
                console.WriteLine(
                    $"  [{Phases.FormatElapsed(e.Elapsed),9}] {e.Table} ({e.TableNumber}/{e.TableCount}): "
                    + $"{e.Rows.ToString("N0", CultureInfo.InvariantCulture)} rows");
                break;
            case NativeConversionEventKind.StageFinished:
                console.WriteLine($"  [{Phases.FormatElapsed(e.Elapsed),9}] {names[e.Stage]}");
                break;
        }
    }
}
