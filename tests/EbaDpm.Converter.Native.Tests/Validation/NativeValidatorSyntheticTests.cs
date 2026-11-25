using System.Security.Cryptography;
using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Native.Catalog;
using EbaDpm.Converter.Native.Tests.Support;
using EbaDpm.Converter.Native.Validation;

namespace EbaDpm.Converter.Native.Tests.Validation;

/// <summary>
/// Plane A of the native output (docs/validation.md, "Native output"), on a small synthetic DPM 2.0
/// Access database and its native conversion. The clean pair passes every check; every check then has
/// a positive control: a tampered copy of the output with one known defect must turn exactly the
/// intended check into <c>fail</c> (and name the object by business key).
/// </summary>
public sealed class NativeValidatorSyntheticTests(NativeValidationFixture fixture) : IClassFixture<NativeValidationFixture>
{
    private static readonly string[] NatIds = ["A-NAT-01", "A-NAT-02", "A-NAT-03", "A-NAT-04", "A-NAT-05"];

    internal static CheckInfo Check(ValidationReport report, string id)
        => Assert.Single(report.Checks, c => c.Id == id);

    private static string Dump(ValidationReport report)
        => string.Join(
            "\n",
            report.Checks.Select(c =>
                $"{c.Id} [{c.Layer}] {c.Status} examined={c.Examined} failed={c.Failed} skip={c.SkipReason}"
                + string.Concat(c.Samples.Take(3).Select(s => $"\n    {s.BusinessKey} | {s.Generated} | {s.Reference}"))));

    private ValidationReport Validate(string output, bool withSource = true)
    {
        try
        {
            return NativeValidator.Validate(output, withSource ? fixture.SourcePath : null);
        }
        finally
        {
            NativeValidationFixture.ReleasePools();
        }
    }

    // ------------------------------------------------------------------ clean pair

    [AceFact]
    public void CleanPair_EveryNativeCheckPasses_ExitCodeZero()
    {
        var report = Validate(fixture.OutputPath);

        foreach (var id in NatIds)
        {
            var check = Check(report, id);
            Assert.True(check.Status == "pass", $"{id} did not pass on the clean pair:\n{Dump(report)}");
            Assert.Equal("critical", check.Layer);
            Assert.Equal(0, check.Failed);
            Assert.True(check.Examined > 0, $"{id} examined nothing (a pass over an empty universe proves nothing).");
        }

        Assert.Equal(0, report.Summary.ExitCode);
        Assert.Equal(0, report.Summary.Critical.Failed);
        Assert.Equal("native", report.OutputKind);
        Assert.NotNull(report.Inputs.Source);
        Assert.Null(report.Inputs.Reference);
        Assert.DoesNotContain(report.Checks, c => c.Plane == "B"); // plane B does not exist for a native output
    }

    [AceFact]
    public void CleanPair_Content_ExaminesEveryRowOfEveryExportedTable()
    {
        // The synthetic source holds: Category 1, Release 2, Table 2, Reference 2, Composite 2, TypeProbe 4,
        // EmptyTable 0, ChangeLogX 1, Aux_Other 1, TableVersionCell 1, GuidKeyed 2, NoKey 2 = 20 rows exported.
        var report = Validate(fixture.OutputPath);

        Assert.Equal(20, Check(report, "A-NAT-03").Examined);
        Assert.Equal(12, Check(report, "A-NAT-02").Examined);
    }

    [AceFact]
    public void CleanPair_DriftIsInformative_NeverFails_AndEqualsTheDirectComputation()
    {
        var report = Validate(fixture.OutputPath);
        var drift = Check(report, "A-DRF-01");
        var expected = NativeDrift.Compute(NativeCatalogReader.Read(fixture.SourcePath));
        NativeValidationFixture.ReleasePools();

        // The tiny synthetic catalogue is nothing like the 4.4 baseline: the drift is large, and it must not matter.
        Assert.Equal("informative", drift.Layer);
        Assert.Equal("info", drift.Status);
        Assert.True(expected.Count > 0);
        Assert.Equal(expected.Count, drift.Failed);
        Assert.Equal(0, report.Summary.ExitCode);
        Assert.Equal(0, report.Summary.Critical.Failed);
    }

    // ------------------------------------------------------------------ positive controls

    private sealed record Tamper(string[] Statements, string CheckId, string SampleContains, bool OthersPass);

    private static readonly string[] RebuildChangeLogXSwapped =
    [
        "CREATE TABLE \"tmp\" AS SELECT * FROM \"ChangeLogX\"",
        "DROP TABLE \"ChangeLogX\"",
        "CREATE TABLE \"ChangeLogX\" (\"Note\" TEXT, \"ChangeLogXID\" INTEGER NOT NULL, PRIMARY KEY (\"ChangeLogXID\"))",
        "INSERT INTO \"ChangeLogX\" (\"Note\", \"ChangeLogXID\") SELECT \"Note\", \"ChangeLogXID\" FROM \"tmp\"",
        "DROP TABLE \"tmp\"",
    ];

    private static readonly string[] RebuildChangeLogXRetyped =
    [
        "CREATE TABLE \"tmp\" AS SELECT * FROM \"ChangeLogX\"",
        "DROP TABLE \"ChangeLogX\"",
        "CREATE TABLE \"ChangeLogX\" (\"ChangeLogXID\" INTEGER NOT NULL, \"Note\" INTEGER, PRIMARY KEY (\"ChangeLogXID\"))",
        "INSERT INTO \"ChangeLogX\" (\"ChangeLogXID\", \"Note\") SELECT \"ChangeLogXID\", \"Note\" FROM \"tmp\"",
        "DROP TABLE \"tmp\"",
    ];

    private static readonly Dictionary<string, Tamper> Cases = new()
    {
        // A-NAT-01: completeness
        ["nat01-missing-table"] = new(["DROP TABLE \"EmptyTable\""], "A-NAT-01", "EmptyTable", false),
        ["nat01-extra-table"] = new(["CREATE TABLE \"Surplus\" (\"x\" INTEGER)"], "A-NAT-01", "Surplus", false),
        ["nat01-excluded-table-present"] = new(["CREATE TABLE \"ChangeLog\" (\"ChangeLogID\" INTEGER)"], "A-NAT-01", "ChangeLog", false),
        ["nat01-missing-column"] = new(["ALTER TABLE \"Aux_Other\" DROP COLUMN \"Note\""], "A-NAT-01", "Aux_Other", false),
        ["nat01-extra-column"] = new(["ALTER TABLE \"Aux_Other\" ADD COLUMN \"Surplus\" TEXT"], "A-NAT-01", "Aux_Other", false),
        ["nat01-reordered-columns"] = new(RebuildChangeLogXSwapped, "A-NAT-01", "ChangeLogX", false),

        // A-NAT-02: row counts
        ["nat02-row-deleted"] = new(["DELETE FROM \"Composite\" WHERE \"A\" = 1"], "A-NAT-02", "Composite", false),
        ["nat02-row-added"] = new(["INSERT INTO \"Composite\" (\"A\", \"B\", \"Label\") VALUES (9, 9, 'z')"], "A-NAT-02", "Composite", false),

        // A-NAT-03: content, by primary-key kind (content tampering must leave the other checks passing)
        ["nat03-integer-pk"] = new(["UPDATE \"Table\" SET \"Code\" = 'T_X' WHERE \"TableID\" = 10"], "A-NAT-03", "Table|pk=TableID=10", true),
        ["nat03-composite-pk"] = new(["UPDATE \"Composite\" SET \"Label\" = 'zz' WHERE \"A\" = 1 AND \"B\" = 2"], "A-NAT-03", "Composite|pk=A=1,B=2", true),
        ["nat03-guid-key"] = new(["UPDATE \"GuidKeyed\" SET \"Name\" = 'changed' WHERE \"Name\" = 'one'"], "A-NAT-03", "GuidKeyed", true),
        ["nat03-no-key-table"] = new(["UPDATE \"NoKey\" SET \"Val\" = 3 WHERE \"Tag\" = 'b'"], "A-NAT-03", "NoKey|row=", true),
        ["nat03-null-became-value"] = new(["UPDATE \"Reference\" SET \"Note\" = 'x' WHERE \"ReferenceID\" = 2"], "A-NAT-03", "Reference|pk=ReferenceID=2", true),
        ["nat03-value-became-null"] = new(["UPDATE \"Reference\" SET \"Note\" = NULL WHERE \"ReferenceID\" = 1"], "A-NAT-03", "Reference|pk=ReferenceID=1", true),
        ["nat03-short-text-trimmed"] = new(["UPDATE \"TypeProbe\" SET \"AsShortText\" = trim(\"AsShortText\") WHERE \"ProbeID\" = 1"], "A-NAT-03", "TypeProbe|pk=ProbeID=1", true),
        ["nat03-short-text-left-trimmed"] = new(["UPDATE \"TypeProbe\" SET \"AsShortText\" = ltrim(\"AsShortText\") WHERE \"ProbeID\" = 1"], "A-NAT-03", "TypeProbe|pk=ProbeID=1", true),
        ["nat03-long-text-trailing-spaces-trimmed"] = new(["UPDATE \"TypeProbe\" SET \"AsLongText\" = rtrim(\"AsLongText\") WHERE \"ProbeID\" = 1"], "A-NAT-03", "TypeProbe|pk=ProbeID=1", true),
        ["nat03-empty-string-became-null"] = new(["UPDATE \"TypeProbe\" SET \"AsShortText\" = NULL WHERE \"ProbeID\" = 3"], "A-NAT-03", "TypeProbe|pk=ProbeID=3", true),
        ["nat03-integer-changed"] = new(["UPDATE \"TypeProbe\" SET \"AsLong\" = \"AsLong\" - 1 WHERE \"ProbeID\" = 1"], "A-NAT-03", "TypeProbe|pk=ProbeID=1", true),
        ["nat03-guid-text-uppercased"] = new(["UPDATE \"TypeProbe\" SET \"RowGUID\" = upper(\"RowGUID\") WHERE \"ProbeID\" = 1"], "A-NAT-03", "TypeProbe|pk=ProbeID=1", true),

        // A-NAT-04: relationships
        ["nat04-orphan-by-update"] = new(["UPDATE \"Reference\" SET \"TableID\" = 999 WHERE \"ReferenceID\" = 1"], "A-NAT-04", "Reference|rowid=", false),
        ["nat04-orphan-by-insert"] = new(["INSERT INTO \"Reference\" (\"ReferenceID\", \"TableID\", \"Note\") VALUES (99, 999, 'orphan')"], "A-NAT-04", "Reference|rowid=", false),

        // A-NAT-05: schema (tables and indexes)
        ["nat05-extra-index"] = new(["CREATE INDEX \"ix_extra\" ON \"Composite\" (\"Label\")"], "A-NAT-05", "index:ix_extra", true),
        ["nat05-missing-index"] = new(["DROP INDEX \"Table_uq_Table_Code\""], "A-NAT-05", "index:Table_uq_Table_Code", true),
        ["nat05-changed-column-type"] = new(RebuildChangeLogXRetyped, "A-NAT-05", "table:ChangeLogX", true),
    };

    public static TheoryData<string> CaseNames => new(Cases.Keys);

    [AceTheory]
    [MemberData(nameof(CaseNames))]
    public void PositiveControl_TamperedOutput_TurnsTheIntendedCheckIntoFail_AndNamesTheObject(string name)
    {
        var tamper = Cases[name];
        var path = fixture.Tamper(tamper.Statements);

        var report = Validate(path);

        var check = Check(report, tamper.CheckId);
        Assert.True(check.Status == "fail", $"{name}: {tamper.CheckId} must fail on the tampered output.\n{Dump(report)}");
        Assert.Equal("critical", check.Layer);
        Assert.True(check.Failed > 0);
        Assert.Contains(
            check.Samples,
            s => s.BusinessKey.Contains(tamper.SampleContains, StringComparison.Ordinal));
        Assert.Equal(3, report.Summary.ExitCode);
        Assert.True(report.Summary.Critical.Failed >= 1);

        if (tamper.OthersPass)
        {
            foreach (var id in NatIds.Where(id => id != tamper.CheckId))
            {
                Assert.True(Check(report, id).Status == "pass", $"{name}: {id} must not be disturbed by this tampering.\n{Dump(report)}");
            }
        }
    }

    [AceFact]
    public void PositiveControl_SameCountDifferentRows_PassesCountButFailsContent()
    {
        // One row deleted and another added: the count is unchanged, so only the content check can see it.
        var path = fixture.Tamper(
            "DELETE FROM \"Composite\" WHERE \"A\" = 1",
            "INSERT INTO \"Composite\" (\"A\", \"B\", \"Label\") VALUES (9, 9, 'z')");

        var report = Validate(path);

        Assert.Equal("pass", Check(report, "A-NAT-02").Status);
        var content = Check(report, "A-NAT-03");
        Assert.Equal("fail", content.Status);
        Assert.Contains(content.Samples, s => s.BusinessKey.Contains("Composite|pk=A=1,B=2", StringComparison.Ordinal) && s.Generated == "<row missing>");
        Assert.Contains(content.Samples, s => s.BusinessKey.Contains("Composite|pk=A=9,B=9", StringComparison.Ordinal) && s.Reference == "<row not in the source>");
        Assert.Equal(3, report.Summary.ExitCode);
    }

    [AceFact]
    public void PositiveControl_ExcludedTableInTheOutput_IsNamedAsExcluded()
    {
        var report = Validate(fixture.Tamper("CREATE TABLE \"ChangeLog\" (\"ChangeLogID\" INTEGER)"));

        var sample = Assert.Single(Check(report, "A-NAT-01").Samples);
        Assert.Equal("ChangeLog", sample.BusinessKey);
        Assert.Equal("<excluded table>", sample.Reference);
    }

    [AceFact]
    public void PositiveControl_OrphanRowIsFoundWithoutSource()
    {
        var path = fixture.Tamper("UPDATE \"Reference\" SET \"TableID\" = 999 WHERE \"ReferenceID\" = 1");

        var report = Validate(path, withSource: false);

        var check = Check(report, "A-NAT-04");
        Assert.Equal("fail", check.Status);
        Assert.Contains(check.Samples, s => s.BusinessKey.StartsWith("Reference|rowid=", StringComparison.Ordinal) && s.Generated!.Contains("Table", StringComparison.Ordinal));
        Assert.Equal(3, report.Summary.ExitCode);
    }

    [AceFact]
    public void PositiveControl_ASourceWithoutTheRows_FailsRowCountAndContent()
    {
        // The reverse direction: the output is clean, the SOURCE differs (same schema, no rows).
        using var other = SyntheticDpm2Accdb.Create(extra: c =>
        {
            SyntheticDpm2Accdb.Execute(c, "CREATE TABLE [TableVersionCell] ([CellID] LONG NOT NULL, [CellCode] LONG, [Note] TEXT(10), CONSTRAINT [pk_TableVersionCell] PRIMARY KEY ([CellID]))");
            SyntheticDpm2Accdb.Execute(c, "ALTER TABLE [Category] ADD COLUMN [ExtraCol] TEXT(10)");
            SyntheticDpm2Accdb.Execute(c, "CREATE TABLE [GuidKeyed] ([GuidID] GUID NOT NULL, [Name] TEXT(20), CONSTRAINT [pk_GuidKeyed] PRIMARY KEY ([GuidID]))");
            SyntheticDpm2Accdb.Execute(c, "CREATE TABLE [NoKey] ([Tag] TEXT(10), [Val] LONG)");
        });

        NativeValidationFixture.ReleasePools(); // ACE still holds a freshly created file open
        var report = NativeValidator.Validate(fixture.OutputPath, other.Path);
        NativeValidationFixture.ReleasePools();

        // The other source has no TableVersionCell/GuidKeyed/NoKey rows: the output has rows the source lacks.
        Assert.Equal("fail", Check(report, "A-NAT-02").Status);
        Assert.Equal("fail", Check(report, "A-NAT-03").Status);
        Assert.Equal(3, report.Summary.ExitCode);
    }

    // ------------------------------------------------------------------ without --source

    [AceFact]
    public void WithoutSource_SourceChecksAreSkippedWithTheReason_NeverPassed()
    {
        var report = Validate(fixture.OutputPath, withSource: false);

        foreach (var id in new[] { "A-NAT-01", "A-NAT-02", "A-NAT-03", "A-DRF-01" })
        {
            var check = Check(report, id);
            Assert.Equal("skipped", check.Status);
            Assert.Contains("--source", check.SkipReason, StringComparison.Ordinal);
            Assert.Equal(0, check.Examined);
        }

        Assert.Null(report.Inputs.Source);
        Assert.Equal("native", report.OutputKind);
    }

    [AceFact]
    public void WithoutSource_ForeignKeysRun_AndSchemaIsInformativeAgainstTheBaseline()
    {
        var report = Validate(fixture.OutputPath, withSource: false);

        var fk = Check(report, "A-NAT-04");
        Assert.Equal("pass", fk.Status);
        Assert.Equal("critical", fk.Layer);

        // The synthetic output is not the 4.4 schema: against the baseline the difference is reported, as information.
        var schema = Check(report, "A-NAT-05");
        Assert.Equal("informative", schema.Layer);
        Assert.Equal("info", schema.Status);
        Assert.True(schema.Failed > 0);
        Assert.Equal(0, report.Summary.ExitCode);
    }

    [AceFact]
    public void WithSource_SchemaCheckIsCritical()
    {
        Assert.Equal("critical", Check(Validate(fixture.OutputPath), "A-NAT-05").Layer);
    }

    [AceFact]
    public void Summary_WithoutSource_CountsSkippedApart_PassedExcludesThem()
    {
        var report = NativeValidator.Validate(fixture.OutputPath);
        NativeValidationFixture.ReleasePools();

        var critical = report.Checks.Where(c => c.Layer == "critical").ToList();
        var skipped = critical.Count(c => c.Status == "skipped");
        var passed = critical.Count(c => c.Status == "pass");
        Assert.Equal(3, skipped); // A-NAT-01..03: derived from the checks, and pinned by the spec
        Assert.Equal(skipped, report.Summary.Critical.Skipped);
        Assert.Equal(passed, report.Summary.Critical.Passed);
        Assert.Equal(critical.Count, report.Summary.Critical.Checks);
        Assert.Equal(0, report.Summary.Critical.Failed);
        Assert.Equal(critical.Count, report.Summary.Critical.Passed + report.Summary.Critical.Failed + report.Summary.Critical.Skipped!.Value);
    }

    [AceFact]
    public void Summary_WithSource_HasNoSkippedCritical()
    {
        var report = Validate(fixture.OutputPath);

        var critical = report.Checks.Where(c => c.Layer == "critical").ToList();
        Assert.DoesNotContain(critical, c => c.Status == "skipped");
        Assert.True((report.Summary.Critical.Skipped ?? 0) == 0);
        Assert.Equal(critical.Count, report.Summary.Critical.Passed);
    }

    [AceFact]
    public void SourceHeldOpenForWriteByAnotherStream_IsStillValidated_WithoutReleasingPools()
    {
        using var holder = new FileStream(fixture.SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        var report = NativeValidator.Validate(fixture.OutputPath, fixture.SourcePath);

        Assert.Equal(0, report.Summary.ExitCode);
        Assert.Equal("pass", Check(report, "A-NAT-03").Status);
        Assert.Equal(new System.IO.FileInfo(fixture.SourcePath).Length, report.Inputs.Source!.SizeBytes);
        NativeValidationFixture.ReleasePools();
    }

    // ------------------------------------------------------------------ the validated file is never modified

    [AceFact]
    public void ValidatedFile_IsNeverModified_CleanOrTampered_WithOrWithoutSource()
    {
        var tampered = fixture.Tamper("UPDATE \"Reference\" SET \"TableID\" = 999 WHERE \"ReferenceID\" = 1");

        foreach (var path in new[] { fixture.OutputPath, tampered })
        {
            foreach (var withSource in new[] { true, false })
            {
                var before = Fingerprint(path);
                var siblingsBefore = Siblings(path);

                _ = Validate(path, withSource);

                Assert.Equal(before, Fingerprint(path));
                Assert.Equal(siblingsBefore, Siblings(path));
            }
        }
    }

    private static (string Hash, long Length, DateTime LastWriteUtc) Fingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        var info = new System.IO.FileInfo(path);
        return (hash, info.Length, info.LastWriteTimeUtc);
    }

    private static string[] Siblings(string path)
        => [.. Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
}
