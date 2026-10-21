using System.Data;

namespace EbaDpm.Converter.Core.Access;

/// <summary>
/// NULL-tolerant read helpers shared by the Access readers (<c>using static</c>).
/// </summary>
internal static class AccessRecordExtensions
{
    public static string? GetNullableString(IDataRecord record, int ordinal)
        => record.IsDBNull(ordinal) ? null : Convert.ToString(record.GetValue(ordinal));

    public static int? GetNullableInt32(IDataRecord record, int ordinal)
        => record.IsDBNull(ordinal) ? null : Convert.ToInt32(record.GetValue(ordinal));

    public static DateTime? GetNullableDateTime(IDataRecord record, int ordinal)
        => record.IsDBNull(ordinal) ? null : Convert.ToDateTime(record.GetValue(ordinal));
}
