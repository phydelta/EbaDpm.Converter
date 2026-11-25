using EbaDpm.Converter.Native.Tests.PlaneC;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.Layouts;

/// <summary>
/// The content comparison of two layout repositories used by the real-data tests
/// (<see cref="LayoutRepositoryComparer"/>), shown able to see a difference.
/// </summary>
public sealed class LayoutRepositoryComparerTests
{
    [Fact]
    public void Identical_ReportsNothing_AndAChangedRowOrADroppedRowIsReported()
    {
        // Positive control of the comparison used below: a repository compared with itself has no
        // difference, with a copy that differs by one value it has exactly that one, and the provenance
        // table LayoutExtraction is the only thing it ignores.
        using var a = new SyntheticLayoutRepository();
        using var b = new SyntheticLayoutRepository();
        foreach (var repo in new[] { a, b })
        {
            var sheet = repo.Sheet("4.4", "T_01.00");
            repo.SignatureCell(sheet, "MET(qAAA)", "qDIM(qM1)");
        }

        a.Connection.Close();
        b.Connection.Close();
        Assert.Empty(LayoutRepositoryComparer.Differences(a.FilePath, b.FilePath));

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = b.FilePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO LayoutExtraction (ExtractionId, DictionaryPath, ExtractedAtUtc) VALUES (1, 'x', 'now')";
            command.ExecuteNonQuery();
        }

        Assert.Empty(LayoutRepositoryComparer.Differences(a.FilePath, b.FilePath)); // provenance is ignored.

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = b.FilePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE LayoutValue SET MemberCode = 'qM2' WHERE MemberCode = 'qM1'";
            command.ExecuteNonQuery();
        }

        var changed = LayoutRepositoryComparer.Differences(a.FilePath, b.FilePath);
        var line = Assert.Single(changed);
        Assert.StartsWith("LayoutValue: 1 row(s) only in the first, 1 only in the second", line);

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = b.FilePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM LayoutCell";
            command.ExecuteNonQuery();
        }

        Assert.Contains(LayoutRepositoryComparer.Differences(a.FilePath, b.FilePath), d => d.StartsWith("LayoutCell: 1 rows against 0", StringComparison.Ordinal));
    }
}
