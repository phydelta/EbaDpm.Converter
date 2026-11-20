using Spectre.Console;
using Spectre.Console.Rendering;

namespace EbaDpm.Converter.Cli;

/// <summary>
/// One output stream of the CLI (standard output or standard error) over an
/// <see cref="IAnsiConsole"/>. It exists in two flavours:
/// <list type="bullet">
///   <item><b>Plain</b>: text goes straight to the <see cref="TextWriter"/>; tables are drawn
///   without borders and nothing is coloured. Used when the stream is redirected (pipes, files,
///   CI, a relaunched child), with <c>--plain</c>, and always by <see cref="CliRunner.Run(string[], TextWriter, TextWriter)"/>
///   so that tests and callers that inject writers get text without escape sequences.</item>
///   <item><b>Rich</b>: an ANSI terminal; tables with borders, colours (unless <c>NO_COLOR</c> is
///   set) and live progress.</item>
/// </list>
/// Text is always written as text, never as markup: business keys contain square brackets.
/// </summary>
internal sealed class CliConsole
{
    private const int PlainWidth = 1000;
    private const int PlainHelpWidth = 100;

    private CliConsole(IAnsiConsole console, TextWriter writer, bool rich)
    {
        Console = console;
        Writer = writer;
        Rich = rich;
    }

    public IAnsiConsole Console { get; }

    public TextWriter Writer { get; }

    /// <summary>An ANSI terminal: borders, colours and (if also interactive) live widgets.</summary>
    public bool Rich { get; }

    /// <summary>Live widgets (progress) need an ANSI terminal that accepts cursor movement.</summary>
    public bool LiveWidgets => Rich && Console.Profile.Capabilities.Interactive;

    /// <summary>Plain output over a writer: no ANSI, no colours, no interactivity.</summary>
    public static CliConsole Plain(TextWriter writer, int width = PlainWidth)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
        });
        console.Profile.Width = width;
        return new CliConsole(console, writer, rich: false);
    }

    /// <summary>
    /// The console of a real standard stream: rich when it is a terminal that supports ANSI,
    /// plain when it is redirected. <c>NO_COLOR</c> (any non-empty value) turns the colours off but
    /// keeps the terminal features.
    /// </summary>
    public static CliConsole Detect(TextWriter writer, bool isRedirected)
    {
        if (isRedirected)
        {
            return Plain(writer);
        }

        var noColor = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Detect,
            ColorSystem = noColor ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
            Interactive = InteractionSupport.Detect,
            Out = new AnsiConsoleOutput(writer),
        });

        return console.Profile.Capabilities.Ansi
            ? new CliConsole(console, writer, rich: true)
            : Plain(writer);
    }

    /// <summary>The same stream forced to plain output (<c>--plain</c>).</summary>
    public CliConsole AsPlain() => Rich ? Plain(Writer) : this;

    /// <summary>
    /// The console to render help on: a plain one is given a width that wraps the option
    /// descriptions readably.
    /// </summary>
    public CliConsole ForHelp() => Rich ? this : Plain(Writer, PlainHelpWidth);

    public void WriteLine()
    {
        if (Rich)
        {
            Console.WriteLine();
        }
        else
        {
            Writer.WriteLine();
        }
    }

    /// <summary>Writes a line of text, as is.</summary>
    public void WriteLine(string text) => WriteStyled(text, null);

    /// <summary>Writes a line of text in the given style (ignored when plain).</summary>
    public void WriteLine(string text, Style? style) => WriteStyled(text, style);

    /// <summary>Writes <c>Error: message</c>, the label in red.</summary>
    public void Error(string message)
    {
        if (!Rich)
        {
            Writer.WriteLine($"Error: {message}");
            return;
        }

        Console.Write(new Text("Error:", new Style(Color.Red, decoration: Decoration.Bold)));
        Console.Write(new Text(" " + message));
        Console.WriteLine();
    }

    /// <summary>Writes a renderable (a table).</summary>
    public void Write(IRenderable renderable)
    {
        if (Rich)
        {
            Console.Write(renderable);
            return;
        }

        // Plain: render to text first and drop the padding at the end of each line, so that a
        // wide last column does not leave trailing blanks in logs and files.
        using var buffer = new StringWriter();
        var scratch = Plain(buffer, Console.Profile.Width);
        scratch.Console.Write(renderable);
        foreach (var line in buffer.ToString().Split(Environment.NewLine).SkipLast(1))
        {
            Writer.WriteLine(line.TrimEnd());
        }
    }

    /// <summary>Writes an exception in full (type, message and stack), for unexpected failures.</summary>
    public void WriteException(Exception exception)
    {
        if (Rich)
        {
            Console.WriteException(exception, ExceptionFormats.ShortenPaths | ExceptionFormats.ShortenTypes);
        }
        else
        {
            Writer.WriteLine(exception.ToString());
        }
    }

    /// <summary>The border for tables: none when plain (one row per line, easy to grep), rounded on a terminal.</summary>
    public TableBorder TableBorder => Rich ? Spectre.Console.TableBorder.Rounded : Spectre.Console.TableBorder.None;

    private void WriteStyled(string text, Style? style)
    {
        if (!Rich)
        {
            Writer.WriteLine(text);
            return;
        }

        Console.Write(new Text(text, style ?? Style.Plain));
        Console.WriteLine();
    }
}
