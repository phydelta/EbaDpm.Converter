using System.Globalization;

namespace EbaDpm.Converter.Native.Schema;

/// <summary>How a value of an Access column is written to SQLite.</summary>
public enum NativeValueForm
{
    /// <summary>The integer (<c>adSmallInt</c>, <c>adInteger</c>, <c>adBigInt</c>, <c>adUnsignedTinyInt</c>).</summary>
    Integer,

    /// <summary>Yes/No: 1 or 0 (<c>adBoolean</c>).</summary>
    Boolean,

    /// <summary>The text as stored, not trimmed (<c>adWChar</c>).</summary>
    Text,

    /// <summary>Replication ID: 36 characters, lower case, no braces (<c>adGUID</c>).</summary>
    Guid,

    /// <summary>The exact decimal in invariant culture, as text (<c>adNumeric</c>).</summary>
    Decimal,

    /// <summary>ISO 8601 <c>yyyy-MM-ddTHH:mm:ss</c>, as text (<c>adDate</c>).</summary>
    DateTime,
}

/// <summary>The SQLite declared type and the value form of one Access type.</summary>
public sealed record NativeColumnMapping(string SqliteType, NativeValueForm Form);

/// <summary>
/// The fixed type mapping of the native output (docs/target-schema-native.md, "Type mapping"),
/// keyed by the OLE DB data type code. A type that is not in it stops the conversion with an
/// error that names the column: no value is ever written in a guessed form.
/// </summary>
public static class NativeTypeMapping
{
    private const int AdSmallInt = 2;
    private const int AdInteger = 3;
    private const int AdDate = 7;
    private const int AdBoolean = 11;
    private const int AdUnsignedTinyInt = 17;
    private const int AdBigInt = 20;
    private const int AdGuid = 72;
    private const int AdWChar = 130;
    private const int AdNumeric = 131;

    private static readonly NativeColumnMapping IntegerMapping = new("INTEGER", NativeValueForm.Integer);
    private static readonly NativeColumnMapping BooleanMapping = new("INTEGER", NativeValueForm.Boolean);
    private static readonly NativeColumnMapping TextMapping = new("TEXT", NativeValueForm.Text);
    private static readonly NativeColumnMapping GuidMapping = new("TEXT", NativeValueForm.Guid);
    private static readonly NativeColumnMapping DecimalMapping = new("TEXT", NativeValueForm.Decimal);
    private static readonly NativeColumnMapping DateTimeMapping = new("TEXT", NativeValueForm.DateTime);

    /// <summary>The mapping of an OLE DB type, or an error naming <c>table.column</c> and the type.</summary>
    /// <exception cref="NotSupportedException">The type is not in the mapping.</exception>
    public static NativeColumnMapping Resolve(string table, string column, int oleDbType)
        => oleDbType switch
        {
            AdSmallInt or AdInteger or AdUnsignedTinyInt or AdBigInt => IntegerMapping,
            AdBoolean => BooleanMapping,
            AdWChar => TextMapping,
            AdGuid => GuidMapping,
            AdNumeric => DecimalMapping,
            AdDate => DateTimeMapping,
            _ => throw new NotSupportedException(
                $"The Access type of column '{table}.{column}' (OLE DB data type {oleDbType}) is not in the native "
                + "type mapping; the conversion stops rather than write it in a guessed form."),
        };

    /// <summary>Converts a value read from Access to the value written to SQLite (<c>null</c> for <c>NULL</c>).</summary>
    public static object? ToSqliteValue(NativeValueForm form, object value)
    {
        if (value is DBNull)
        {
            return null;
        }

        return form switch
        {
            NativeValueForm.Integer => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            NativeValueForm.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? 1L : 0L,
            NativeValueForm.Text => (string)value,
            NativeValueForm.Guid => value switch
            {
                Guid guid => guid.ToString("D", CultureInfo.InvariantCulture),
                _ => throw new InvalidCastException($"A Replication ID came as {value.GetType().Name}, not as a Guid."),
            },
            NativeValueForm.Decimal => FormatDecimal(Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
            NativeValueForm.DateTime => Convert.ToDateTime(value, CultureInfo.InvariantCulture)
                .ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null),
        };
    }

    /// <summary>Exact decimal text without trailing zeros (<c>0.5</c>, <c>12</c>), invariant culture.</summary>
    internal static string FormatDecimal(decimal value)
        => (value / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
}
