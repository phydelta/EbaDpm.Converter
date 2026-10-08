using System.Data.OleDb;

namespace EbaDpm.Converter.Tests;

/// <summary>
/// Message used to skip the tests that need the Microsoft Access Database Engine.
/// </summary>
internal static class AceRequirement
{
    public const string SkipReason =
        "Requires the Microsoft Access Database Engine (Microsoft.ACE.OLEDB.16.0, x64). See docs/test-data.md.";

    private const string ProviderName = "Microsoft.ACE.OLEDB.16.0";

    private static readonly Lazy<bool> _isAvailable = new(Detect);

    /// <summary>
    /// True when both ADOX and the ACE 16.0 OLEDB provider are registered. Cached; never throws.
    /// </summary>
    public static bool IsAvailable => _isAvailable.Value;

    private static bool Detect()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            if (Type.GetTypeFromProgID("ADOX.Catalog") is null)
            {
                return false;
            }

            using var table = new OleDbEnumerator().GetElements();
            foreach (System.Data.DataRow row in table.Rows)
            {
                if (string.Equals(row["SOURCES_NAME"]?.ToString(), ProviderName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // Fallback: the provider's own ProgID.
            return Type.GetTypeFromProgID(ProviderName) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped when the Access Database Engine (ACE OLEDB 16.0 x64)
/// is not installed (e.g. GitHub-hosted runners).
/// </summary>
public sealed class AceFactAttribute : FactAttribute
{
    public AceFactAttribute()
    {
        if (!AceRequirement.IsAvailable)
        {
            Skip = AceRequirement.SkipReason;
        }
    }
}
