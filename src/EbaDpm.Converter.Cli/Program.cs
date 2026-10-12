using System.Runtime.InteropServices;
using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Core.Access;

// An ACE provider only serves a process of its own bitness: when this x64 process has none but
// a 32-bit one is installed (32-bit Office), hand over to the x86 sibling. The probe never throws.
if (ShouldRelaunchToX86())
{
    var siblingPath = ArchitectureRelauncher.SiblingPath();
    try
    {
        return ArchitectureRelauncher.Relaunch(siblingPath, args);
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        Console.Error.WriteLine($"Error: could not start the 32-bit executable '{siblingPath}': {ex.Message}");
        return CliRunner.ExitArgumentError;
    }
}

return CliRunner.Run(args, Console.Out, Console.Error);

static bool ShouldRelaunchToX86()
{
    try
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        if (architecture != Architecture.X64
            || Environment.GetEnvironmentVariable(ArchitectureRelauncher.RelaunchedVariable) == "1")
        {
            return false;
        }

        var siblingExists = File.Exists(ArchitectureRelauncher.SiblingPath());
        if (!siblingExists)
        {
            return false;
        }

        return ArchitectureRelauncher.ShouldRelaunch(
            AceProvider.RegisteredForCurrentProcess().Count > 0,
            AceProvider.IsRegisteredForOtherArchitecture(),
            architecture,
            alreadyRelaunched: false,
            siblingExists);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or PlatformNotSupportedException or COMException)
    {
        return false;
    }
}
