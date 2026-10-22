using System.Data;
using System.Data.OleDb;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EbaDpm.Converter.Core.Access;

/// <summary>
/// Resolves the ACE OLEDB provider used to read the source <c>.accdb</c>. The tool does not require
/// a specific redistributable: it uses whichever provider the machine offers (typically the one
/// installed with Microsoft Office, 32-bit or 64-bit), preferring 16.0 over 12.0. A provider is
/// only usable by a process of the same bitness, which is why the CLI can relaunch itself as x86.
/// </summary>
public static class AceProvider
{
    private static readonly object Gate = new();
    private static string? _resolved;

    /// <summary>Preference order.</summary>
    public static IReadOnlyList<string> Candidates { get; } = ["Microsoft.ACE.OLEDB.16.0", "Microsoft.ACE.OLEDB.12.0"];

    /// <summary>
    /// Connection string for <paramref name="accdbPath"/> with the resolved provider.
    /// </summary>
    public static string ConnectionString(string accdbPath) =>
        $"Provider={Resolve(accdbPath)};Data Source={accdbPath};Persist Security Info=False;";

    /// <summary>
    /// Resolves the provider (cached for the process after the first success; thread-safe).
    /// </summary>
    public static string Resolve(string accdbPath)
    {
        lock (Gate)
        {
            if (_resolved is not null)
            {
                return _resolved;
            }

            var registered = RegisteredForCurrentProcess();
            string provider;
            try
            {
                provider = Select(registered, p => TryOpen(p, accdbPath), accdbPath, IsRegisteredForOtherArchitecture());
            }
            catch (AceProviderNotFoundException) when (registered.Count > 0)
            {
                // A provider exists for this process but could not open THIS file (missing path,
                // not an Access database...). That is a problem with the file, not with the
                // provider: hand back the preferred registered one, without caching it, so that
                // the caller's own open reports the file error under its usual handling.
                return registered.First();
            }

            _resolved = provider;
            return provider;
        }
    }

    /// <summary>
    /// Pure selection logic. Walks <see cref="Candidates"/> in order; a candidate is tried only if
    /// it is in <paramref name="registered"/> (case-insensitive); <paramref name="tryOpen"/> returns
    /// null on success or the exception it hit. Returns the first candidate that opens.
    /// </summary>
    /// <exception cref="AceProviderNotFoundException">No candidate opened.</exception>
    public static string Select(
        IReadOnlyCollection<string> registered,
        Func<string, Exception?> tryOpen,
        string accdbPath,
        bool otherArchitectureHasProvider)
    {
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(tryOpen);

        var registeredSet = new HashSet<string>(registered, StringComparer.OrdinalIgnoreCase);
        var attempts = new List<(string Provider, string Outcome)>();

        foreach (var candidate in Candidates)
        {
            if (!registeredSet.Contains(candidate))
            {
                attempts.Add((candidate, "not registered"));
                continue;
            }

            var error = tryOpen(candidate);
            if (error is null)
            {
                return candidate;
            }

            attempts.Add((candidate, error.Message));
        }

        throw new AceProviderNotFoundException(accdbPath, attempts, otherArchitectureHasProvider);
    }

    /// <summary>
    /// Candidates registered for the CURRENT process bitness (the OLEDB enumerator, SOURCES_NAME),
    /// in preference order.
    /// </summary>
    public static IReadOnlyCollection<string> RegisteredForCurrentProcess()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var table = new OleDbEnumerator().GetElements();
            foreach (DataRow row in table.Rows)
            {
                if (row["SOURCES_NAME"] is string name)
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception ex) when (ex is OleDbException or InvalidOperationException or COMException
                                       or PlatformNotSupportedException or TypeInitializationException)
        {
            return [];
        }

        return Candidates.Where(names.Contains).ToList();
    }

    /// <summary>
    /// True if any candidate is registered in the OTHER registry view (HKCR; the 32-bit view when
    /// the process is 64-bit and vice versa): the key <c>&lt;ProgID&gt;\CLSID</c> exists.
    /// </summary>
    public static bool IsRegisteredForOtherArchitecture()
    {
        try
        {
            var view = Environment.Is64BitProcess ? RegistryView.Registry32 : RegistryView.Registry64;
            using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            return Candidates.Any(candidate =>
            {
                using var key = root.OpenSubKey($@"{candidate}\CLSID");
                return key is not null;
            });
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
                                       or IOException or PlatformNotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static Exception? TryOpen(string provider, string accdbPath)
    {
        try
        {
            using var connection = new OleDbConnection(
                $"Provider={provider};Data Source={accdbPath};Persist Security Info=False;");
            connection.Open();
            connection.Close();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}

/// <summary>
/// Raised when no ACE OLEDB provider candidate can open the source file.
/// </summary>
public sealed class AceProviderNotFoundException : Exception
{
    public AceProviderNotFoundException(
        string accdbPath,
        IReadOnlyList<(string Provider, string Outcome)> attempts,
        bool otherArchitectureHasProvider)
        : base(BuildMessage(accdbPath, attempts, otherArchitectureHasProvider))
    {
        Attempts = attempts;
        OtherArchitectureHasProvider = otherArchitectureHasProvider;
    }

    /// <summary>Per candidate: "not registered" or the message of the open error.</summary>
    public IReadOnlyList<(string Provider, string Outcome)> Attempts { get; }

    /// <summary>A candidate is registered for the other process architecture (32-bit vs 64-bit).</summary>
    public bool OtherArchitectureHasProvider { get; }

    private static string BuildMessage(
        string accdbPath,
        IReadOnlyList<(string Provider, string Outcome)> attempts,
        bool otherArchitectureHasProvider)
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        var lines = string.Join(
            Environment.NewLine,
            attempts.Select(a => $"  {a.Provider}: {a.Outcome}"));
        var advice = otherArchitectureHasProvider
            ? "An ACE OLEDB provider is installed for the other architecture (32-bit vs 64-bit "
              + "Microsoft Office): use the matching executable (x86 or x64) of this tool."
            : "Install Microsoft Office (which includes the Access database engine) or the "
              + "Microsoft Access Database Engine redistributable.";

        return $"Could not open '{accdbPath}': no usable ACE OLEDB provider for this {architecture} "
            + $"process. Providers tried:{Environment.NewLine}{lines}{Environment.NewLine}{advice}";
    }
}
