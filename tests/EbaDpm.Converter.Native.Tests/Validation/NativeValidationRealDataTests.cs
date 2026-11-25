using System.Data.OleDb;
using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Native.Catalog;
using EbaDpm.Converter.Native.Tests.Support;
using EbaDpm.Converter.Native.Validation;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.Validation;

/// <summary>
/// Plane A and the drift report on the real 4.4 database (<c>DPM2 Database_v 4_4_20261006.accdb</c>) and its
/// native conversion (shared fixture: one 45 s conversion for all the native data tests), and the drift
/// positive control on the 4.3 database.
/// </summary>
[Collection(Native44Collection.Name)]
public sealed class NativeValidationRealDataTests(Native44Fixture fixture)
{
    private static readonly object Gate = new();
    private static ValidationReport? _withSource;

    private void RequireConverted()
        => Assert.True(fixture.Converted, "The 4.4 conversion did not run although the data directory exists (is ACE installed?).");

    private ValidationReport WithSource()
    {
        lock (Gate)
        {
            return _withSource ??= ValidateAgainstSource(fixture.OutputPath);
        }
    }

    private ValidationReport ValidateAgainstSource(string output)
    {
        // ACE keeps the .accdb open while a connection is pooled, and the validator hashes the source first.
        NativeValidationFixture.ReleasePools();
        try
        {
            return NativeValidator.Validate(output, fixture.SourcePath);
        }
        finally
        {
            NativeValidationFixture.ReleasePools();
        }
    }

    private static string Dump(ValidationReport report)
        => string.Join(
            "\n",
            report.Checks.Select(c =>
                $"{c.Id} [{c.Layer}] {c.Status} examined={c.Examined} failed={c.Failed}"
                + string.Concat(c.Samples.Take(5).Select(s => $"\n    {s.BusinessKey} | {s.Generated} | {s.Reference}"))));

    [DataFact]
    public void Release44_Validation_EveryCriticalCheckPasses_AndDriftIsEmpty()
    {
        RepoPaths.EnsureAccessDpm20Release44DatabaseExists();
        RequireConverted();

        var report = WithSource();

        foreach (var id in new[] { "A-NAT-01", "A-NAT-02", "A-NAT-03", "A-NAT-04", "A-NAT-05" })
        {
            var check = Assert.Single(report.Checks, c => c.Id == id);
            Assert.True(check.Status == "pass", $"{id}:\n{Dump(report)}");
            Assert.Equal("critical", check.Layer);
            Assert.True(check.Examined > 0, $"{id} examined nothing.");
        }

        var drift = Assert.Single(report.Checks, c => c.Id == "A-DRF-01");
        Assert.True(drift.Failed == 0, $"The 4.4 database must not drift against the 4.4 baseline:\n{Dump(report)}");
        Assert.Equal("pass", drift.Status);
        Assert.Equal(0, report.Summary.ExitCode);
        Assert.Equal("native", report.OutputKind);
        Assert.NotNull(report.Inputs.Source);
    }

    [DataFact]
    public void Release44_Validation_CoversTheWholeExportedUniverse()
    {
        RepoPaths.EnsureAccessDpm20Release44DatabaseExists();
        RequireConverted();

        var report = WithSource();

        // 59 exported tables (70 in Access minus the 11 excluded); the content check examined real volume.
        Assert.Equal(59, Assert.Single(report.Checks, c => c.Id == "A-NAT-02").Examined);
        var rows = Assert.Single(report.Checks, c => c.Id == "A-NAT-03").Examined;
        Assert.True(rows > 1_000_000, $"A-NAT-03 examined only {rows} rows.");
    }

    [DataFact]
    public void Release44_WithoutSource_SourceChecksAreSkipped_AndTheRestPass()
    {
        RepoPaths.EnsureAccessDpm20Release44DatabaseExists();
        RequireConverted();

        var report = NativeValidator.Validate(fixture.OutputPath);

        foreach (var id in new[] { "A-NAT-01", "A-NAT-02", "A-NAT-03", "A-DRF-01" })
        {
            Assert.Equal("skipped", Assert.Single(report.Checks, c => c.Id == id).Status);
        }

        Assert.Equal("pass", Assert.Single(report.Checks, c => c.Id == "A-NAT-04").Status);
        var schema = Assert.Single(report.Checks, c => c.Id == "A-NAT-05");
        Assert.Equal("informative", schema.Layer);
        Assert.True(schema.Status == "pass", Dump(report));
        Assert.Equal(0, report.Summary.ExitCode);
    }

    [DataFact]
    public void Release44_PositiveControl_ATamperedCopy_IsRejected()
    {
        RepoPaths.EnsureAccessDpm20Release44DatabaseExists();
        RequireConverted();

        // The same validation on a copy with one value changed and one row deleted in two other tables.
        var copy = Path.Join(Path.GetDirectoryName(fixture.OutputPath)!, "tampered44.db");
        File.Copy(fixture.OutputPath, copy, overwrite: true);
        using (var connection = AccessSqliteComparer.OpenSqlite(copy, readOnly: false))
        {
            Exec(connection, "UPDATE \"Release\" SET \"Code\" = \"Code\" || ' ' WHERE \"ReleaseID\" = (SELECT MIN(\"ReleaseID\") FROM \"Release\")");
            Exec(connection, "DELETE FROM \"Category\" WHERE \"CategoryID\" = (SELECT MAX(\"CategoryID\") FROM \"Category\")");
        }

        SqliteConnection.ClearAllPools();

        var report = ValidateAgainstSource(copy);

        Assert.Equal(3, report.Summary.ExitCode);
        Assert.Equal("fail", Assert.Single(report.Checks, c => c.Id == "A-NAT-02").Status);
        var content = Assert.Single(report.Checks, c => c.Id == "A-NAT-03");
        Assert.Equal("fail", content.Status);
        Assert.Contains(content.Samples, s => s.BusinessKey.StartsWith("Release|pk=ReleaseID=", StringComparison.Ordinal));
        Assert.Contains(content.Samples, s => s.BusinessKey.StartsWith("Category|pk=CategoryID=", StringComparison.Ordinal) && s.Generated == "<row missing>");
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    // ------------------------------------------------------------------ drift positive control on 4.3

    [DataFact]
    public void Release43_Drift_IsNotEmpty_AndNamesTheTablesThatDifferFromTheBaseline()
    {
        RepoPaths.EnsureAccessDpm20Release43DatabaseExists();

        var catalog = NativeCatalogReader.Read(RepoPaths.AccessDpm20Release43DatabasePath);
        OleDbConnection.ReleaseObjectPool();
        var drift = NativeDrift.Compute(catalog);

        Assert.NotEmpty(drift);
        // In 4.3, not in the 4.4 baseline.
        Assert.Contains(drift, d => d.Kind == NativeCatalogDifferenceKind.TableAdded && d.Subject == "VariableCalculation");
        // In the 4.4 baseline, not in 4.3.
        Assert.Contains(drift, d => d.Kind == NativeCatalogDifferenceKind.TableRemoved && d.Subject == "OperationOutput");
        Assert.DoesNotContain(NativeBaseline.Load().Catalog.Tables, t => t.Name == "VariableCalculation");
        Assert.Contains(NativeBaseline.Load().Catalog.Tables, t => t.Name == "OperationOutput");
    }
}
