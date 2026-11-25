using EbaDpm.Converter.Native.Catalog;
using EbaDpm.Converter.Native.Schema;

namespace EbaDpm.Converter.Native.Conversion;

/// <summary>The native output exists only for DPM 2.0 sources; the source is DPM 1.0.</summary>
public sealed class NativeSourceNotDpm20Exception(string sourcePath)
    : Exception($"The native output exists only for DPM 2.0 sources; '{sourcePath}' is a DPM 1.0 database.")
{
    public string SourcePath { get; } = sourcePath;
}

/// <summary>
/// The source has no release with the baseline <c>Release.Code</c>: it is older than the 4.4
/// schema the native output starts from.
/// </summary>
public sealed class NativeSourceBelowBaselineException : Exception
{
    public NativeSourceBelowBaselineException(string sourcePath, string baselineReleaseCode, string? currentReleaseCode)
        : base(
            $"The native output starts from the {baselineReleaseCode} schema: '{sourcePath}' has no release with code "
            + $"'{baselineReleaseCode}' in its Release table (its current release is "
            + $"{(currentReleaseCode is null ? "not declared" : $"'{currentReleaseCode}'")}). "
            + $"Use a publication that contains release {baselineReleaseCode}.")
    {
        SourcePath = sourcePath;
        BaselineReleaseCode = baselineReleaseCode;
        CurrentReleaseCode = currentReleaseCode;
    }

    public string SourcePath { get; }

    public string BaselineReleaseCode { get; }

    /// <summary><c>Release.Code</c> of the row with <c>IsCurrent</c> set, or null when there is none.</summary>
    public string? CurrentReleaseCode { get; }
}

public enum NativeConversionStage
{
    ReadCatalogue,
    CreateSchema,
    CopyTables,
    CreateIndexes,
    Vacuum,
}

public enum NativeConversionEventKind
{
    /// <summary>A stage starts (<see cref="NativeConversionEvent.TableCount"/> is set for <see cref="NativeConversionStage.CopyTables"/>).</summary>
    StageStarted,

    /// <summary>A stage ends.</summary>
    StageFinished,

    /// <summary>A table was copied and verified (<see cref="NativeConversionEvent.Table"/>, <see cref="NativeConversionEvent.Rows"/>).</summary>
    TableCopied,
}

/// <summary>Progress notification of <see cref="NativeConverter.Convert"/>, raised on the converting thread.</summary>
public sealed record NativeConversionEvent(
    NativeConversionEventKind Kind,
    NativeConversionStage Stage,
    string? Table = null,
    int TableNumber = 0,
    int TableCount = 0,
    long Rows = 0,
    TimeSpan Elapsed = default);

/// <summary>A table written to the output.</summary>
public sealed record NativeTableResult(string Name, long Rows, int Columns, TimeSpan Elapsed);

/// <summary>What a native conversion did.</summary>
/// <param name="Tables">The tables written, in the order they were copied (ordinal by name).</param>
/// <param name="ExcludedTablesInSource">The tables of the closed exclusion list that the source has, not written.</param>
/// <param name="Catalog">The catalogue of the source that the DDL was built from (drift: compare with the baseline).</param>
/// <param name="Schema">The DDL that was run, with the foreign keys declared and dropped.</param>
/// <param name="Drift">The differences between the baseline catalogue and <paramref name="Catalog"/>
/// (<see cref="NativeDrift.Compute"/>); informative, empty when the source has the baseline schema.</param>
/// <param name="StageElapsed">Elapsed time of each stage.</param>
public sealed record NativeConversionSummary(
    string OutputPath,
    string CurrentReleaseCode,
    IReadOnlyList<NativeTableResult> Tables,
    IReadOnlyList<string> ExcludedTablesInSource,
    NativeCatalog Catalog,
    NativeSchemaDdl Schema,
    IReadOnlyList<NativeCatalogDifference> Drift,
    IReadOnlyDictionary<NativeConversionStage, TimeSpan> StageElapsed,
    TimeSpan Elapsed,
    long FileSizeBytes)
{
    public long TotalRows => Tables.Sum(t => t.Rows);
}
