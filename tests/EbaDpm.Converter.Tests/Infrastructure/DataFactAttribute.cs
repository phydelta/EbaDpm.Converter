using EbaDpm.Converter.Tests.Schema;

namespace EbaDpm.Converter.Tests;

/// <summary>
/// Message used to skip the tests that need the EBA data files.
/// </summary>
internal static class DataRequirement
{
    public const string SkipReason =
        "Requires the EBA DPM data files (set EBADPM_TEST_DATA or create ./Data). See docs/test-data.md.";
}

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped when the data directory is not available.
/// </summary>
public sealed class DataFactAttribute : FactAttribute
{
    public DataFactAttribute()
    {
        if (!RepoPaths.IsDataAvailable)
        {
            Skip = DataRequirement.SkipReason;
        }
    }
}

/// <summary>
/// A <see cref="TheoryAttribute"/> that is skipped when the data directory is not available.
/// </summary>
public sealed class DataTheoryAttribute : TheoryAttribute
{
    public DataTheoryAttribute()
    {
        if (!RepoPaths.IsDataAvailable)
        {
            Skip = DataRequirement.SkipReason;
        }
    }
}
