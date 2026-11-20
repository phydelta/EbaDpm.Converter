using Spectre.Console.Cli;

namespace EbaDpm.Converter.Cli;

/// <summary>The two output streams of one run, handed to the command through the settings.</summary>
internal sealed record CliContext(CliConsole Stdout, CliConsole Stderr);

/// <summary>
/// The only command: the mode is chosen by the flags in <see cref="ConverterSettings"/>, so existing
/// invocations keep working.
/// </summary>
internal sealed class ConverterCommand : Command<ConverterSettings>
{
    public override int Execute(CommandContext context, ConverterSettings settings, CancellationToken cancellationToken)
        => CliRunner.Execute(settings);
}

/// <summary>
/// Gives the settings the consoles of the run, and the tokens the parser could not match with an
/// option (reported by the mode as unrecognized arguments).
/// </summary>
internal sealed class ContextInterceptor(CliContext context) : ICommandInterceptor
{
    public void Intercept(CommandContext commandContext, CommandSettings settings)
    {
        if (settings is not ConverterSettings converterSettings)
        {
            return;
        }

        converterSettings.Context = context;
        converterSettings.UnknownTokens = DescribeUnknown(commandContext.Remaining);
    }

    private static List<string> DescribeUnknown(IRemainingArguments remaining)
    {
        var unknown = new List<string>();
        foreach (var group in remaining.Parsed)
        {
            // The key is the unmatched option as typed (with its dashes); a stray value has no name.
            unknown.Add(group.Key);
            unknown.AddRange(group.Where(v => v is not null).Select(v => v!));
        }

        if (unknown.Count == 0)
        {
            unknown.AddRange(remaining.Raw);
        }

        return unknown;
    }
}
