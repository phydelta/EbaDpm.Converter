using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Tests.All;
using EbaDpm.Converter.Tests.Dpm2;
using EbaDpm.Converter.Tests.Support;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// <c>C-DPS-01</c> is no longer skipped on DPM 1.0 (the undetermined-release guard is now
/// EVALUATED instead of skipped). Asserting that it passes is not enough: both directions must be
/// asserted --
/// <list type="bullet">
/// <item>on a correct DPM 1.0 output with the real 3.2 layout repository (the same corpus that
/// measured 472 tables / 86,115 signatures / 0 violations), <c>C-DPS-01</c> is EVALUATED (not
/// <c>skipped</c>) and has <c>Examined &gt; 0</c>;</item>
/// <item>and it WOULD FAIL: on a COPY of that same output, the pair <c>MRW(eba_AP:x93)</c> (the
/// real witness case of <c>C_106.00</c> / <c>sbp 3.2.1</c>, already pinned in
/// <c>mOrdinateCategorisation</c> by the fixed-pairs sentinel test) is removed from the
/// <c>mTableCell.DPS</c> cells that carry it -- <c>PlaneCComparer</c> reads the signature from
/// <c>mTableCell.DPS</c>, not from <c>mOrdinateCategorisation</c>, so the mutation has to happen
/// there. Same positive-control pattern as the other sentinel tests and as
/// <see cref="PlaneCCensusGateTests"/>: <c>Data/</c> is never touched, only copies in
/// <c>%TEMP%</c>.</item>
/// <item>and on DPM 2.0 4.3 (a WELL-DEFINED release, different from the 4.2 of the known-divergence
/// census) it STILL comes out <c>skipped</c> -- guard (b) of <c>PlaneCChecks.BuildCDps01</c> is
/// unchanged and identical on 4.2 and 4.3.</item>
/// </list>
///
/// The positive control that appeared by itself shows exactly this mutation spontaneously:
/// comparing an older output (from before the fixed-pair fix) against the same 3.2 repository gave
/// 9 violations -- 7 in <c>C_106.00</c>, all with <c>MRW(x93)</c>, and 2 in <c>C_110.03</c> with
/// <c>HYV(x10)</c>. This class builds that SAME situation on purpose instead of depending on an old
/// output existing by accident.
/// </summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class CDps01Dpm10SentinelRealDataTests(AllFixture fixture) : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), $"EbaDpm.CDps01Dpm10Sentinel_{Environment.ProcessId}_{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort.
        }
    }

    /// <summary>
    /// First direction: on the REAL (unmutated) DPM 1.0 output and the REAL 3.2 layout repository,
    /// <c>C-DPS-01</c> is EVALUATED -- neither <c>skipped</c> for "0 tables compared" (guard a) nor
    /// for "well-defined but different release" (guard b, which does not apply: DPM 1.0 has no
    /// single cut-off release) -- and examines more than zero signatures. Since 0 violations were
    /// already measured on this same corpus, <c>Failed == 0</c> is asserted here too: if it stops
    /// being 0, the positive control below (which DOES expect a failure) would lose its clean
    /// baseline.
    /// </summary>
    [DataFact]
    public void CDps01_OnRealDpm10WithReal32Layouts_IsEvaluatedWithPositiveExaminedAndZeroViolations()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null, PlaneCLayout32RepositoryHolder.LayoutsDatabasePath);

        var cDps01 = Assert.Single(result.Report.Checks, c => c.Id == "C-DPS-01");
        var coverage = Assert.Single(result.Report.Checks, c => c.Id == "C-COB-01");

        Assert.NotEqual("skipped", cDps01.Status);
        Assert.True(cDps01.Examined > 0, $"C-DPS-01.Examined={cDps01.Examined} (expected > 0) -- {coverage.Statement}");
        Assert.True(cDps01.Failed == 0,
            $"C-DPS-01.Failed={cDps01.Failed} (expected 0) -- {coverage.Statement}. " +
            $"Samples: [{string.Join(" || ", cDps01.Samples.Select(s => $"{s.BusinessKey}={s.Reference}"))}]");
        Assert.Equal("pass", cDps01.Status);
    }

    /// <summary>
    /// Second direction -- the positive control: on a COPY of the real DPM 1.0 output,
    /// <c>MRW(eba_AP:x93)</c> is removed from all the <c>mTableCell.DPS</c> values of
    /// <c>C_106.00</c> that carry it (the same fixed pair). <c>C-DPS-01</c> must turn to
    /// <c>fail</c>, with at least one sample whose business key names <c>C_106.00</c>.
    /// </summary>
    [DataFact]
    public void CDps01_RemovingMrwX93FromC10600_FailsWithTheViolationInC10600()
    {
        Directory.CreateDirectory(_tempDirectory);
        var mutatedPath = Path.Combine(_tempDirectory, "generated-mutated.db");

        SqliteConnection.ClearAllPools();
        File.Copy(fixture.ValidatedDatabasePath, mutatedPath);

        // Measured beforehand: mTableCell.DPS carries the "eba_dim:" prefix on each dimension term
        // ("eba_dim:MRW(eba_AP:x93)") -- UNLIKE mOrdinateCategorisation.DPS, which the fixed-pairs
        // sentinel test checks WITHOUT prefix ("MRW(eba_AP:x93)"). PlaneCComparer normalizes this
        // prefix in the comparison (PlaneCSignature.StripEbaPrefix handles "eba_dim:" just like
        // "eba_"; its character class includes ".", "_" on purpose), but the MUTATION has to target
        // the RAW literal of mTableCell.DPS, which does carry it.
        const string rawTerm = "eba_dim:MRW(eba_AP:x93)";
        var removedCount = RemoveTermFromTableCellDps(mutatedPath, tableCode: "C_106.00", term: rawTerm);

        // Universe positive control: if this is 0, the mutation touched nothing and the rest of the
        // test proves nothing -- the witness case would have disappeared from the corpus (for
        // example if C_106.00 were renamed or the fixed pair changed).
        Assert.True(removedCount > 0,
            $"0 rows of mTableCell.DPS of C_106.00 carried '{rawTerm}': the witness case " +
            "is not in the corpus, or its shape changed.");

        SqliteConnection.ClearAllPools();
        var result = Validator.Run(mutatedPath, referencePath: null, PlaneCLayout32RepositoryHolder.LayoutsDatabasePath);

        var cDps01 = Assert.Single(result.Report.Checks, c => c.Id == "C-DPS-01");

        Assert.Equal("fail", cDps01.Status);
        Assert.True(cDps01.Failed > 0, $"C-DPS-01.Failed={cDps01.Failed} (expected > 0 after removing MRW(eba_AP:x93)).");
        Assert.Contains(cDps01.Samples, s => s.BusinessKey.StartsWith("C_106.00\t", StringComparison.Ordinal));
    }

    /// <summary>
    /// Removes the term <paramref name="term"/> from each <c>mTableCell.DPS</c> of the tables with
    /// <paramref name="tableCode"/> that carry it (separator <c>|</c>; the terms do not nest
    /// parentheses, so splitting on <c>|</c> and filtering the exact term is safe). Never touches
    /// <c>Data/</c> -- <paramref name="databasePath"/> is always a copy in <c>%TEMP%</c>. Returns
    /// how many rows were mutated (universe positive control).
    /// </summary>
    private static int RemoveTermFromTableCellDps(string databasePath, string tableCode, string term)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();

        var rows = new List<(long CellId, string Dps)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText =
                """
                SELECT tc."CellID", tc."DPS"
                FROM "mTableCell" tc
                JOIN "mTable" t ON t."TableID" = tc."TableID"
                WHERE t."TableCode" = $tableCode AND tc."DPS" LIKE $likeTerm
                """;
            select.Parameters.AddWithValue("$tableCode", tableCode);
            select.Parameters.AddWithValue("$likeTerm", $"%{term}%");
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        using var transaction = connection.BeginTransaction();
        foreach (var (cellId, dps) in rows)
        {
            var terms = dps.Split('|').Where(t => t != term).ToArray();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """UPDATE "mTableCell" SET "DPS" = $newDps WHERE "CellID" = $cellId""";
            update.Parameters.AddWithValue("$newDps", string.Join("|", terms));
            update.Parameters.AddWithValue("$cellId", cellId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return rows.Count;
    }
}

/// <summary>
/// Third direction: on DPM 2.0 4.3 (a WELL-DEFINED release, different from the 4.2 of the
/// known-divergence census), <c>C-DPS-01</c> STILL comes out <c>skipped</c> -- this guard is
/// unchanged (explicit comment in <c>PlaneCChecks</c>: unchanged, identical on 4.2 and 4.3).
/// Complements <see cref="PlaneCCrossOutputDpm2043Tests"/> (which already checks
/// <c>Failed == 0</c> but does not pin the literal <c>Status</c>) without touching its assertions.
/// </summary>
[Collection("Dpm2043ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class CDps01Dpm2043StillSkippedRealDataTests(Dpm2043ValidateFixture fixture)
{
    [DataFact]
    public void CDps01_OnDpm2043WithLayouts42_KeepsBeingSkipped_Unchanged()
    {
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, referencePath: null, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);

        var cDps01 = Assert.Single(result.Report.Checks, c => c.Id == "C-DPS-01");

        Assert.Equal("skipped", cDps01.Status);
        Assert.False(string.IsNullOrWhiteSpace(cDps01.SkipReason));
        Assert.Contains("plane C census", cDps01.SkipReason!, StringComparison.Ordinal);
    }
}
