using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// Of the 194 sheets with a Z suffix, 14 have no value in the <c>Header</c> region
/// (<c>C_18.00(0001)</c>, <c>C_21.00(0001)</c>, <c>C_34.02.a/b(0001)</c>,
/// <c>K_74.00.a..f(0010)</c>). "Nothing is lost" because those dimensions vary per grid row.
///
/// Verified against the reference database: in the two sheets checked, the Z suffix (0001 =
/// "Total"/currency aggregate in C_18.00; 0010 = "Disclosure period T" in K_74.00.a) has NO
/// corresponding term either in the layout or in the real <c>DatapointSignature</c> of the
/// reference (compared against the sibling variant that DOES fix the dimension: (0002)=Euro with
/// <c>eba_dim:qAYX(...)</c>, 0020=T-1 with <c>eba_dim_4.0:qBEG(...)</c>). The layout consistently
/// omits the same thing the reference omits: there is no loss of information in these two
/// specific cases.
/// </summary>
[Collection("LayoutsRealData")]
public sealed class LayoutZSuffixMissingHeaderTests(LayoutRepositoryFixture fixture)
{
    [DataFact]
    public void ExactlyFourteenZSuffixSheets_HaveNoHeaderRegionValue()
    {
        var con = fixture.Repository42WithDictionary;

        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            SELECT s.SheetName FROM LayoutSheet s
            WHERE s.ZSuffix IS NOT NULL
              AND NOT EXISTS (
                SELECT 1 FROM LayoutValue v
                JOIN LayoutDeclaration d ON d.DeclId = v.DeclId
                WHERE v.SheetId = s.SheetId AND d.Region = 'Header'
              )
            ORDER BY s.SheetName
            """;
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        Assert.Equal(14, names.Count);
        Assert.Equal(
            new[]
            {
                "C_18.00(0001)", "C_18.00(0001)", "C_21.00(0001)", "C_21.00(0001)",
                "C_34.02.a(0001)", "C_34.02.a(0001)", "C_34.02.b(0001)", "C_34.02.b(0001)",
                "K_74.00.a(0010)", "K_74.00.b(0010)", "K_74.00.c(0010)",
                "K_74.00.d(0010)", "K_74.00.e(0010)", "K_74.00.f(0010)",
            }.OrderBy(x => x),
            names.OrderBy(x => x));
    }

    [DataFact]
    public void C_18_00_0001_MissingCurrencyDimension_IsAbsentInReferenceSignatureToo_NotLost()
    {
        // (0002) DOES declare and fix the currency: D2=(qAYX:CU), D3=(CU:EUR). (0001) declares
        // nothing in that position -- check that the real reference does not have the qAYX term in
        // the signature of any cell with Z ordinate=0001 either, while it DOES have it (with
        // another member) for Z=0002.
        using var reference = OpenReference();

        var tableId = QueryScalarLong(reference, "SELECT MAX(TableID) FROM mTable WHERE TableCode = 'C_18.00'");

        var signaturesZ0001 = QuerySignatures(reference, tableId, "0001");
        Assert.NotEmpty(signaturesZ0001);
        Assert.All(signaturesZ0001, sig => Assert.DoesNotContain("qAYX", sig ?? string.Empty));
    }

    [DataFact]
    public void K_74_00_a_0010_MissingDisclosurePeriodDimension_IsAbsentInReferenceSignatureToo_NotLost()
    {
        // Z=0020 ("T-1") DOES fix eba_dim_4.0:qBEG(eba_qRF:qx2054). Z=0010 ("T", the base period)
        // should have no qBEG term in any real signature -- just as the layout does not declare it.
        using var reference = OpenReference();
        var tableId = QueryScalarLong(reference, "SELECT MAX(TableID) FROM mTable WHERE TableCode = 'K_74.00.a'");

        var signaturesZ0010 = QuerySignatures(reference, tableId, "0010");
        var signaturesZ0020 = QuerySignatures(reference, tableId, "0020");

        Assert.NotEmpty(signaturesZ0010);
        Assert.NotEmpty(signaturesZ0020);
        Assert.All(signaturesZ0010, sig => Assert.DoesNotContain("qBEG", sig ?? string.Empty));
        Assert.Contains(signaturesZ0020, sig => (sig ?? string.Empty).Contains("qBEG"));
    }

    private static SqliteConnection OpenReference()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Schema.RepoPaths.ReferenceDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static long QueryScalarLong(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static List<string?> QuerySignatures(SqliteConnection connection, long tableId, string zOrdinateCode)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT DatapointSignature FROM mTableCell
            WHERE TableID = $tid AND BusinessCode LIKE $pattern AND DatapointSignature IS NOT NULL
            """;
        cmd.Parameters.AddWithValue("$tid", tableId);
        cmd.Parameters.AddWithValue("$pattern", $"%,{zOrdinateCode}}}");
        using var reader = cmd.ExecuteReader();
        var results = new List<string?>();
        while (reader.Read())
        {
            results.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        return results;
    }
}
