using System.Globalization;
using EbaDpm.Converter.Native.Schema;

namespace EbaDpm.Converter.Native.Tests.Schema;

/// <summary>
/// The type mapping of docs/target-schema-native.md ("Type mapping"): each Access type has one
/// SQLite declared type and one value form; a type outside the table stops the conversion with an
/// error that names the column.
/// </summary>
public sealed class NativeTypeMappingTests
{
    [Theory]
    [InlineData(2, "INTEGER", NativeValueForm.Integer)] // adSmallInt (Integer)
    [InlineData(3, "INTEGER", NativeValueForm.Integer)] // adInteger (Long integer)
    [InlineData(17, "INTEGER", NativeValueForm.Integer)] // adUnsignedTinyInt (Byte)
    [InlineData(20, "INTEGER", NativeValueForm.Integer)] // adBigInt (Large number)
    [InlineData(11, "INTEGER", NativeValueForm.Boolean)] // adBoolean (Yes/No)
    [InlineData(130, "TEXT", NativeValueForm.Text)] // adWChar (short and long text)
    [InlineData(72, "TEXT", NativeValueForm.Guid)] // adGUID (Replication ID)
    [InlineData(131, "TEXT", NativeValueForm.Decimal)] // adNumeric (Decimal): TEXT, never REAL
    [InlineData(7, "TEXT", NativeValueForm.DateTime)] // adDate (Date/Time)
    public void Resolve_MapsEachDocumentedAccessType(int oleDbType, string sqliteType, NativeValueForm form)
    {
        var mapping = NativeTypeMapping.Resolve("T", "C", oleDbType);

        Assert.Equal(sqliteType, mapping.SqliteType);
        Assert.Equal(form, mapping.Form);
    }

    [Theory]
    [InlineData(6)] // adCurrency
    [InlineData(4)] // adSingle
    [InlineData(5)] // adDouble
    [InlineData(128)] // adBinary
    [InlineData(204)] // adVarBinary
    [InlineData(0)] // adEmpty
    public void Resolve_UnmappedType_FailsNamingTableAndColumn(int oleDbType)
    {
        var ex = Assert.Throws<NotSupportedException>(() => NativeTypeMapping.Resolve("Money", "Amount", oleDbType));

        Assert.Contains("'Money.Amount'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(oleDbType.ToString(CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_PositiveControl_TheErrorNamesTheColumnItWasAskedFor()
    {
        // Two different columns give two different messages: the check can tell them apart.
        var a = Assert.Throws<NotSupportedException>(() => NativeTypeMapping.Resolve("T1", "C1", 6));
        var b = Assert.Throws<NotSupportedException>(() => NativeTypeMapping.Resolve("T2", "C2", 6));

        Assert.DoesNotContain("T2.C2", a.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("T1.C1", b.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NativeValueForm.Integer)]
    [InlineData(NativeValueForm.Boolean)]
    [InlineData(NativeValueForm.Text)]
    [InlineData(NativeValueForm.Guid)]
    [InlineData(NativeValueForm.Decimal)]
    [InlineData(NativeValueForm.DateTime)]
    public void ToSqliteValue_DbNull_IsNull_ForEveryForm(NativeValueForm form)
        => Assert.Null(NativeTypeMapping.ToSqliteValue(form, DBNull.Value));

    [Fact]
    public void ToSqliteValue_Integers_AreInt64_AcrossAllAccessIntegerWidths()
    {
        Assert.Equal(255L, Assert.IsType<long>(NativeTypeMapping.ToSqliteValue(NativeValueForm.Integer, (byte)255)));
        Assert.Equal(-32768L, Assert.IsType<long>(NativeTypeMapping.ToSqliteValue(NativeValueForm.Integer, short.MinValue)));
        Assert.Equal(int.MinValue, Assert.IsType<long>(NativeTypeMapping.ToSqliteValue(NativeValueForm.Integer, int.MinValue)));
        Assert.Equal(2147483647L, Assert.IsType<long>(NativeTypeMapping.ToSqliteValue(NativeValueForm.Integer, int.MaxValue)));
        Assert.Equal(long.MaxValue, Assert.IsType<long>(NativeTypeMapping.ToSqliteValue(NativeValueForm.Integer, long.MaxValue)));
        Assert.Equal(0L, Assert.IsType<long>(NativeTypeMapping.ToSqliteValue(NativeValueForm.Integer, 0)));
    }

    [Fact]
    public void ToSqliteValue_Boolean_TrueIsOne_FalseIsZero()
    {
        Assert.Equal(1L, NativeTypeMapping.ToSqliteValue(NativeValueForm.Boolean, true));
        Assert.Equal(0L, NativeTypeMapping.ToSqliteValue(NativeValueForm.Boolean, false));
    }

    [Theory]
    [InlineData("  padded  ")]
    [InlineData("trailing   ")]
    [InlineData("   leading")]
    [InlineData("")]
    [InlineData("a\r\nb\t")]
    [InlineData("   ")]
    public void ToSqliteValue_Text_IsNotTrimmed(string text)
        => Assert.Equal(text, NativeTypeMapping.ToSqliteValue(NativeValueForm.Text, text));

    [Theory]
    [InlineData("0e40d86d-889c-498e-ae66-46398e615cee")]
    [InlineData("0E40D86D-889C-498E-AE66-46398E615CEE")]
    [InlineData("{0E40D86D-889C-498E-AE66-46398E615CEE}")]
    public void ToSqliteValue_Guid_IsLowerCase36CharactersWithoutBraces(string input)
    {
        var value = Assert.IsType<string>(NativeTypeMapping.ToSqliteValue(NativeValueForm.Guid, Guid.Parse(input)));

        Assert.Equal("0e40d86d-889c-498e-ae66-46398e615cee", value);
        Assert.Equal(36, value.Length);
        Assert.DoesNotContain('{', value);
        Assert.DoesNotContain('}', value);
    }

    [Theory]
    [InlineData("0.1", "0.1")]
    [InlineData("0.5", "0.5")]
    [InlineData("12", "12")]
    [InlineData("12.50", "12.5")]
    [InlineData("0", "0")]
    [InlineData("-0.5", "-0.5")]
    [InlineData("123456789.1234567891", "123456789.1234567891")] // 19 digits
    [InlineData("0.0000000000000000001", "0.0000000000000000001")]
    [InlineData("1234567890123456789012345.6789", "1234567890123456789012345.6789")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")] // decimal.MaxValue
    [InlineData("-79228162514264337593543950335", "-79228162514264337593543950335")]
    public void ToSqliteValue_Decimal_IsExactText(string input, string expected)
    {
        var value = decimal.Parse(input, CultureInfo.InvariantCulture);

        Assert.Equal(expected, Assert.IsType<string>(NativeTypeMapping.ToSqliteValue(NativeValueForm.Decimal, value)));
    }

    [Fact]
    public void ToSqliteValue_Decimal_PositiveControl_ABinaryFloatWouldHaveLostThePrecision()
    {
        // The check above is only meaningful if a lossy conversion would fail it: show that a
        // double conversion changes the 19-digit and the maximum values (so the exact-text assertion can fail).
        var nineteen = 123456789.1234567891m;

        Assert.NotEqual("123456789.1234567891", ((double)nineteen).ToString("R", CultureInfo.InvariantCulture));
        Assert.NotEqual("79228162514264337593543950335", ((double)decimal.MaxValue).ToString("F0", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    public void ToSqliteValue_DecimalAndDate_AreInvariantOfTheCurrentCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            Assert.Equal("0.1", NativeTypeMapping.ToSqliteValue(NativeValueForm.Decimal, 0.1m));
            Assert.Equal("123456789.1234567891", NativeTypeMapping.ToSqliteValue(NativeValueForm.Decimal, 123456789.1234567891m));
            Assert.Equal(
                "2026-11-25T13:45:59",
                NativeTypeMapping.ToSqliteValue(NativeValueForm.DateTime, new DateTime(2026, 11, 25, 13, 45, 59)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(2026, 11, 25, 13, 45, 59, "2026-11-25T13:45:59")]
    [InlineData(1999, 1, 2, 0, 0, 0, "1999-01-02T00:00:00")]
    [InlineData(2000, 2, 29, 23, 59, 59, "2000-02-29T23:59:59")]
    [InlineData(1900, 1, 1, 1, 2, 3, "1900-01-01T01:02:03")]
    public void ToSqliteValue_DateTime_IsIso8601WithoutZoneOrFraction(int y, int mo, int d, int h, int mi, int s, string expected)
        => Assert.Equal(expected, NativeTypeMapping.ToSqliteValue(NativeValueForm.DateTime, new DateTime(y, mo, d, h, mi, s)));
}
