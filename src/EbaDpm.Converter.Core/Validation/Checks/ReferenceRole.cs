namespace EbaDpm.Converter.Core.Validation.Checks;

/// <summary>
/// Precedence among references: the file name identifies the role played by the reference given
/// to <c>--reference</c>, exactly as the test fixtures do (content: the 3.2 rules; where it does
/// not reach, the 4.0; the 4.2 never decides content, only schema).
/// </summary>
public static class ReferenceRole
{
    public const string Reference32 = "EBA_3.2_phase_1.db";
    public const string Reference40 = "EBA_4.0_ERRATA_5.db";
    public const string Reference42 = "EBA_4.2_Hotfix.db";

    /// <summary>Resolves the canonical role name from the file name given in <c>--reference</c>.</summary>
    public static string Resolve(string referencePath)
    {
        var name = Path.GetFileName(referencePath);
        if (name.Contains("3.2", StringComparison.OrdinalIgnoreCase))
        {
            return Reference32;
        }

        if (name.Contains("4.0", StringComparison.OrdinalIgnoreCase))
        {
            return Reference40;
        }

        if (name.Contains("4.2", StringComparison.OrdinalIgnoreCase))
        {
            return Reference42;
        }

        return name;
    }

    /// <summary>The 4.2 is the only one with the "schema" role; it never decides content.</summary>
    public static bool IsSchemaRole(string role) => role == Reference42;
}
