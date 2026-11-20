using System.Diagnostics;
using System.Globalization;
using Spectre.Console;

namespace EbaDpm.Converter.Cli;

/// <summary>Runs the named phases of a long operation, one after the other, reporting each.</summary>
internal interface IPhaseRunner
{
    /// <summary>Runs <paramref name="work"/> as the phase <paramref name="name"/> and returns its result.</summary>
    T Run<T>(string name, Func<T> work);

    /// <summary>Runs <paramref name="work"/> as the phase <paramref name="name"/>.</summary>
    void Run(string name, Action work)
        => Run<object?>(name, () =>
        {
            work();
            return null;
        });
}

/// <summary>
/// Reports the phases of a conversion (read the source, load each area, VACUUM...) with the elapsed
/// time of each one. The phases are the stages the CLI itself orchestrates, so Core needs no
/// progress API. On a terminal they are a live progress list; otherwise one line per finished
/// phase, so that a log shows where a run that takes minutes is.
/// </summary>
internal static class Phases
{
    public static void Run(CliConsole console, IReadOnlyList<string> names, Action<IPhaseRunner> body)
    {
        if (!console.LiveWidgets)
        {
            body(new PlainPhaseRunner(console));
            return;
        }

        console.Console
            .Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ElapsedTimeColumn(),
                new SpinnerColumn { CompletedText = "done" })
            .Start(context =>
            {
                var tasks = names.ToDictionary(
                    name => name,
                    name => context.AddTask(name, autoStart: false, maxValue: 1),
                    StringComparer.Ordinal);
                body(new LivePhaseRunner(tasks));
            });
    }

    /// <summary>Elapsed time as shown to the user: <c>0.4 s</c>, <c>12.3 s</c>, <c>2 min 03 s</c>.</summary>
    public static string FormatElapsed(TimeSpan elapsed)
        => elapsed.TotalSeconds < 60
            ? string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalSeconds:0.0} s")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds:00} s");

    private sealed class PlainPhaseRunner(CliConsole console) : IPhaseRunner
    {
        public T Run<T>(string name, Func<T> work)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = work();
            console.WriteLine($"  [{FormatElapsed(stopwatch.Elapsed),9}] {name}");
            return result;
        }
    }

    private sealed class LivePhaseRunner(IReadOnlyDictionary<string, ProgressTask> tasks) : IPhaseRunner
    {
        public T Run<T>(string name, Func<T> work)
        {
            var task = tasks[name];
            task.IsIndeterminate = true;
            task.StartTask();
            var result = work();
            task.IsIndeterminate = false;
            task.Value = task.MaxValue;
            task.StopTask();
            return result;
        }
    }
}
