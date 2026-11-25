using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Distribution.Validation;
using Spectre.Console;

namespace EbaDpm.Converter.Cli;

/// <summary>
/// The styles of the report output. They have meaning, not just colour: green is a pass, blue a
/// known exception that applies, yellow an informative difference or something to look at, red a
/// critical difference or a stale exception (the causes of exit code 3). Ignored when the output
/// is plain.
/// </summary>
internal static class CliStyles
{
    public static readonly Style Plain = Style.Plain;
    public static readonly Style Heading = new(decoration: Decoration.Bold);
    public static readonly Style Pass = new(Color.Green);
    public static readonly Style KnownException = new(Color.Blue);
    public static readonly Style Informative = new(Color.Yellow);
    public static readonly Style Critical = new(Color.Red, decoration: Decoration.Bold);
    public static readonly Style Dim = new(Color.Grey);
}

/// <summary>Tables of the console output (the text of the cells is never interpreted as markup).</summary>
internal static class ReportTable
{
    /// <summary>A table with the given column headers; the columns flagged are right-aligned.</summary>
    public static Table Create(CliConsole console, params (string Header, bool RightAligned)[] columns)
    {
        var table = new Table { Border = console.TableBorder };
        foreach (var (header, rightAligned) in columns)
        {
            var column = new TableColumn(new Text(header, console.Rich ? CliStyles.Heading : Style.Plain)) { NoWrap = false };
            if (rightAligned)
            {
                column.RightAligned();
            }

            table.AddColumn(column);
        }

        return table;
    }

    public static Text Cell(string text, Style? style = null) => new(text, style ?? Style.Plain);

    public static Text Cell(long number, Style? style = null) => new(number.ToString(System.Globalization.CultureInfo.InvariantCulture), style ?? Style.Plain);
}

/// <summary>
/// The conversion summary: one table of what was written, by area, followed by the cases that are
/// documented as not fully resolved (listed, not hidden and not forced).
/// </summary>
internal sealed class ConversionSummary
{
    private readonly CliConsole _console;
    private readonly Table _table;
    private readonly List<(string Title, IReadOnlyList<string> Cases)> _diagnostics = [];
    private string? _lastArea;

    public ConversionSummary(CliConsole console)
    {
        _console = console;
        _table = ReportTable.Create(console, ("Area", false), ("Item", false), ("Rows", true), ("Note", false));
    }

    /// <summary>Adds a row; <paramref name="area"/> is shown only on the first row of its area.</summary>
    public void Add(string area, string item, long rows, string? note = null, Style? style = null)
        => AddRow(area, item, ReportTable.Cell(rows, style), note, style);

    /// <summary>Adds a row without a count (a statement about the area).</summary>
    public void AddStatement(string area, string item, string statement, Style? style = null)
        => AddRow(area, item, ReportTable.Cell(string.Empty), statement, style);

    private void AddRow(string area, string item, Text rows, string? note, Style? style)
    {
        var shownArea = string.Equals(area, _lastArea, StringComparison.Ordinal) ? string.Empty : area;
        _lastArea = area;
        _table.AddRow(
            ReportTable.Cell(shownArea, _console.Rich ? CliStyles.Heading : Style.Plain),
            ReportTable.Cell(item),
            rows,
            ReportTable.Cell(note ?? string.Empty, style ?? CliStyles.Dim));
    }

    /// <summary>Adds a titled list of cases, written after the table; an empty list is not written.</summary>
    public void AddDiagnostics(string title, IReadOnlyList<string> cases)
    {
        if (cases.Count > 0)
        {
            _diagnostics.Add((title, cases));
        }
    }

    public void Write()
    {
        _console.Write(_table);

        foreach (var (title, cases) in _diagnostics)
        {
            _console.WriteLine($"  {title}: {cases.Count} case(s).", CliStyles.Informative);
            foreach (var item in cases.Take(20))
            {
                _console.WriteLine($"    - {item}");
            }

            if (cases.Count > 20)
            {
                _console.WriteLine($"    ... and {cases.Count - 20} more.");
            }
        }
    }
}

/// <summary>The console rendering of the <c>--validate</c> report. The JSON report is not affected.</summary>
internal static class ValidationConsoleReport
{
    public static void Print(CliConsole stdout, ValidationReport report)
    {
        var native = report.OutputKind == "native";
        var referenceText = report.Inputs.Reference is { } reference
            ? $"against '{Path.GetFileName(reference.Path)}'"
            : native
                ? report.Inputs.Source is { } source
                    ? $"(native DPM 2.0 output) against the source '{Path.GetFileName(source.Path)}'"
                    : "(native DPM 2.0 output) without source: the checks against the source are skipped"
                : "without reference (plane A only)";
        stdout.WriteLine($"Validation of '{Path.GetFileName(report.Inputs.Generated.Path)}' {referenceText}.", CliStyles.Heading);
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
                stdout.WriteLine($"WARNING: {notice}", CliStyles.Informative);
                stdout.WriteLine();
            }
        }

        PrintPlane(stdout, report, "A", native
            ? "PLANE A - native output: content against the source, relationships, schema, drift"
            : "PLANE A - internal consistency (no reference)");
        if (native)
        {
            PrintSchemaDrift(stdout, report);
        }

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
        PrintExceptions(stdout, report);

        stdout.WriteLine();
        var criticalFailed = report.Summary.Critical.Failed;
        var informativeFailed = report.Summary.Informative.Failed;
        var staleText = report.Summary.StaleExceptions > 0 ? $" {report.Summary.StaleExceptions} stale exception(s)." : string.Empty;
        var clean = criticalFailed == 0 && report.Summary.StaleExceptions == 0;
        var criticalSkipped = native ? report.Summary.Critical.Skipped ?? 0 : 0;
        var skippedText = criticalSkipped > 0
            ? $" {criticalSkipped} critical check(s) skipped{(report.Inputs.Source is null ? " (needs --source)" : string.Empty)}."
            : string.Empty;
        stdout.WriteLine(
            clean
                ? $"RESULT: no critical differences.{skippedText} {informativeFailed} informative differences in the report."
                : $"RESULT: {criticalFailed} critical differences.{staleText}{skippedText} {informativeFailed} informative differences in the report.",
            clean ? new Style(Color.Green, decoration: Decoration.Bold) : CliStyles.Critical);

        // What plane B no longer fails is counted SEPARATELY, never diluted in the figures above
        // (which remain the total over the three planes).
        if (report.Summary.PlaneBCriticalFailed > 0 || report.Summary.PlaneBStaleExceptions > 0)
        {
            stdout.WriteLine(
                $"  Of the above, PLANE B (does not fail the run): {report.Summary.PlaneBCriticalFailed} failed critical check(s), "
                + $"{report.Summary.PlaneBStaleExceptions} stale exception(s).",
                CliStyles.Informative);
        }
    }

    /// <summary>The schema drift list of a native output, in full (it is short): informative, never a failure.</summary>
    private static void PrintSchemaDrift(CliConsole stdout, ValidationReport report)
    {
        var drift = report.Checks.FirstOrDefault(c => c.Id == "A-DRF-01");
        if (drift is null || drift.Status == "skipped")
        {
            return;
        }

        stdout.WriteLine();
        if (drift.Failed == 0)
        {
            stdout.WriteLine("Schema drift against the 4.4 baseline: none.");
        }
        else
        {
            stdout.WriteLine($"Schema drift against the 4.4 baseline: {drift.Failed} difference(s) (informative).", CliStyles.Informative);
            foreach (var sample in drift.Samples)
            {
                stdout.WriteLine($"  - {sample.BusinessKey}: {sample.Reference ?? "-"} -> {sample.Generated ?? "-"}");
            }

            if (drift.Failed > drift.Samples.Count)
            {
                stdout.WriteLine($"  ... and {drift.Failed - drift.Samples.Count} more (see report).");
            }
        }

        stdout.WriteLine();
    }

    private static void PrintPlane(CliConsole stdout, ValidationReport report, string plane, string header)
    {
        stdout.WriteLine(header, CliStyles.Heading);

        // The native report counts skipped checks apart (they examined nothing); the 45-table
        // report keeps its historical table, where a skipped check counts as passed.
        var native = report.OutputKind == "native";
        var table = native
            ? ReportTable.Create(stdout, ("Layer", false), ("Checks", true), ("Passed", true), ("Skipped", true), ("With differences", true))
            : ReportTable.Create(stdout, ("Layer", false), ("Checks", true), ("Passed", true), ("With differences", true));
        var failingByLayer = new List<(string Layer, List<CheckInfo> Failing)>();
        foreach (var layer in new[] { "critical", "informative" })
        {
            var checks = report.Checks.Where(c => c.Plane == plane && c.Layer == layer).ToList();
            var skipped = native ? checks.Count(c => c.Status == "skipped") : 0;
            var passed = checks.Count(c => c.Status == "pass" || (!native && c.Status == "skipped"));
            var failed = checks.Count - passed - skipped;
            var differencesStyle = failed == 0
                ? CliStyles.Pass
                : layer == "critical" ? CliStyles.Critical : CliStyles.Informative;

            if (native)
            {
                table.AddRow(
                    ReportTable.Cell(layer),
                    ReportTable.Cell(checks.Count),
                    ReportTable.Cell(passed),
                    ReportTable.Cell(skipped),
                    ReportTable.Cell(failed, differencesStyle));
            }
            else
            {
                table.AddRow(
                    ReportTable.Cell(layer),
                    ReportTable.Cell(checks.Count),
                    ReportTable.Cell(passed),
                    ReportTable.Cell(failed, differencesStyle));
            }
            failingByLayer.Add((layer, checks.Where(c => c.Status == "fail" || c.Status == "info").Take(20).ToList()));
        }

        stdout.Write(table);

        foreach (var (layer, failingChecks) in failingByLayer)
        {
            var style = layer == "critical" ? CliStyles.Critical : CliStyles.Informative;
            foreach (var failing in failingChecks)
            {
                stdout.WriteLine($"    {failing.Id}: {failing.Statement} ({failing.Failed} of {failing.Examined})", style);
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

    private static void PrintExceptions(CliConsole stdout, ValidationReport report)
    {
        var current = report.Exceptions.Count(e => e.AppliesToThisReference && !e.Stale);
        var stale = report.Exceptions.Count(e => e.Stale);
        var notEvaluated = report.Exceptions.Count(e => !e.Evaluated);
        var identifiers = report.Exceptions.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count();
        stdout.WriteLine(
            $"Exceptions: {identifiers} identifiers, {report.Exceptions.Count} objects; "
            + $"{current} current, {stale} stale, {notEvaluated} NOT EVALUATED",
            stale > 0 ? CliStyles.Critical : Style.Plain);

        // Grouped by Id on the console - listing hundreds of lines is as useless as listing none.
        // The object-by-object detail lives ENTIRELY in the JSON (--report); only the STALE ones,
        // which require action, are named individually here.
        if (report.Exceptions.Count > 0)
        {
            var table = ReportTable.Create(stdout, ("Id", false), ("Objects", true), ("Status", false), ("Reason", false));
            foreach (var group in report.Exceptions.GroupBy(e => e.Id, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var first = group.First();
                var groupEvaluated = group.Any(e => e.Evaluated);
                var (status, style) = !groupEvaluated
                    ? ("NOT EVALUATED", CliStyles.Informative)
                    : group.Any(e => e.Stale)
                        ? ("STALE", CliStyles.Critical)
                        : group.All(e => e.AppliesToThisReference)
                            ? ("applied", CliStyles.KnownException)
                            : ("does not apply to this reference", CliStyles.Dim);
                table.AddRow(
                    ReportTable.Cell(group.Key),
                    ReportTable.Cell(group.Count()),
                    ReportTable.Cell(status, style),
                    ReportTable.Cell(first.Reason));
            }

            stdout.Write(table);
        }

        // The obligatory counterpart of plane B no longer failing the run is that it be NAMED:
        // check (Scope) and declared reference of each stale exception, one by one, marking the
        // plane B ones as those that no longer fail the run.
        var staleExceptionSamples = report.Exceptions.Where(e => e.Stale).ToList();
        if (staleExceptionSamples.Count > 0)
        {
            stdout.WriteLine("  Stale, one by one:", CliStyles.Critical);
            foreach (var exception in staleExceptionSamples)
            {
                var gateNote = exception.Plane == "B" ? " [plane B: reports, does NOT fail the run]" : "";
                stdout.WriteLine(
                    $"    {exception.Id,-6}{exception.BusinessKey,-45} check={exception.Scope} reference={exception.Reference}{gateNote} {exception.Reason}");
            }
        }
    }
}
