using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation;

/// <summary>
/// Which source model the database opened by <c>--validate</c> comes from. It fixes the SCOPE of the
/// checks that do not apply to DPM 2.0: the DPM 1.0 form of each check is kept unchanged.
///
/// Distinct from <see cref="EbaDpm.Converter.Core.Access.DpmSourceModel"/>: that one detects the
/// model of the SOURCE <c>.accdb</c>, at CONVERSION time, by looking at the Access catalog. This
/// one reads the model of the already GENERATED SQLite database, at <c>--validate</c> time, by
/// looking at a row of <c>aDatabaseProperties</c>. The names are separate on purpose so they are
/// not confused.
/// </summary>
public enum ValidationSourceModel
{
    /// <summary>
    /// The default. The ABSENCE of the <see cref="ValidationSourceModelDetector"/> row in
    /// <c>aDatabaseProperties</c> MEANS DPM 1.0: the DPM 1.0 pipeline writes no marker and its output
    /// does not change by a single byte.
    /// </summary>
    Dpm1,

    Dpm2,
}

/// <summary>
/// Reads the source model from a row of <c>aDatabaseProperties</c>, the same key-value table the
/// schema contract already uses for <c>Validation syntax version</c>. It does not touch the schema
/// contract (which fixes columns, not rows) nor the CLI contract.
///
/// The DPM 2.0 loader (<c>Dpm20SkeletonLoader</c>) writes the row
/// (<see cref="PropertyName"/>, <see cref="Dpm2Value"/>) next to the <c>Validation syntax version</c>
/// one. The DPM 1.0 pipeline is NOT touched: it never writes this row.
/// </summary>
public static class ValidationSourceModelDetector
{
    /// <summary>Exact name of the property in <c>aDatabaseProperties</c>.</summary>
    public const string PropertyName = "Source model";

    /// <summary>Value that <c>Dpm20SkeletonLoader</c> writes to mark a DPM 2.0 source.</summary>
    public const string Dpm2Value = "DPM 2.0";

    public static ValidationSourceModel Detect(SqliteConnection connection)
    {
        var tableExists = SqlHelpers.Scalar(
            connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'aDatabaseProperties'");
        if (tableExists == 0)
        {
            return ValidationSourceModel.Dpm1;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"aDatabaseProperties\" WHERE \"Property\" = @property AND \"Value\" = @value";
        command.Parameters.AddWithValue("@property", PropertyName);
        command.Parameters.AddWithValue("@value", Dpm2Value);
        var matches = Convert.ToInt64(command.ExecuteScalar());
        return matches > 0 ? ValidationSourceModel.Dpm2 : ValidationSourceModel.Dpm1;
    }
}
