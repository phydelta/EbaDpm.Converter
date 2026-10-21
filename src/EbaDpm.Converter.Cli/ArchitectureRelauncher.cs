using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EbaDpm.Converter.Cli;

/// <summary>
/// An ACE OLEDB provider is usable only by a process of the same bitness. The release ships the
/// x64 executable at the root and an x86 build in <c>x86\</c>: when only a 32-bit provider (32-bit
/// Office) is present, the x64 process hands over to its x86 sibling.
/// </summary>
public static class ArchitectureRelauncher
{
    public const string RelaunchedVariable = "EBADPM_RELAUNCHED";

    /// <summary>
    /// Pure decision: relaunch only when the current process has no candidate registered, the
    /// other architecture has one, the process is X64, it was not already relaunched, and the
    /// sibling exists.
    /// </summary>
    public static bool ShouldRelaunch(
        bool currentHasProvider,
        bool otherHasProvider,
        Architecture processArchitecture,
        bool alreadyRelaunched,
        bool siblingExists) =>
        !currentHasProvider
        && otherHasProvider
        && processArchitecture == Architecture.X64
        && !alreadyRelaunched
        && siblingExists;

    /// <summary>Path of the x86 sibling.</summary>
    public static string SiblingPath() =>
        Path.Join(AppContext.BaseDirectory, "x86", "EbaDpm.Converter.exe");

    /// <summary>
    /// Starts the sibling with the same arguments, inheriting the console, and returns its exit
    /// code.
    /// </summary>
    public static int Relaunch(string siblingPath, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo(siblingPath)
        {
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment[RelaunchedVariable] = "1";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{siblingPath}'.");
        process.WaitForExit();
        return process.ExitCode;
    }
}
