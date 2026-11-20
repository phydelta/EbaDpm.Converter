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

// A terminal gets colours, tables with borders and live progress; redirected output (pipes,
// files, CI, a relaunched child) stays plain text.
var code = CliRunner.Run(
    args,
    CliConsole.Detect(Console.Out, Console.IsOutputRedirected),
    CliConsole.Detect(Console.Error, Console.IsErrorRedirected));

// Issue #82: once the ACE OLEDB provider has served a connection, the native teardown at process
// exit (an access violation in the Office module mso20win32client.dll, then a fail-fast in ntdll)
// replaces the exit code with 0xC0000409. Disposing every connection and calling
// OleDbConnection.ReleaseObjectPool() does not avoid it, so end the process with the computed
// code without running the native DLL detach.
if (AceProviderLoaded())
{
    Console.Out.Flush();
    Console.Error.Flush();
    TerminateProcess(GetCurrentProcess(), (uint)code);
}

return code;

static bool AceProviderLoaded()
{
    try
    {
        foreach (System.Diagnostics.ProcessModule module in System.Diagnostics.Process.GetCurrentProcess().Modules)
        {
            if (module.ModuleName.StartsWith("ACE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                   or NotSupportedException)
    {
        Console.Error.WriteLine($"Warning: failed to inspect loaded modules to detect ACE provider: {ex.Message}");
    }

    return false;
}

[DllImport("kernel32.dll")]
static extern IntPtr GetCurrentProcess();

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool TerminateProcess(IntPtr process, uint exitCode);

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
