using System.Globalization;
using System.Text.RegularExpressions;
using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Native.PlaneC;
using EbaDpm.Converter.Native.Tests.Layouts;
using EbaDpm.Converter.Native.Tests.Support;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.PlaneC;

/// <summary>
/// The native plane C on the real native 4.4 output (the file the shared <see cref="Native44Fixture"/>
/// converted) against the real EBA Annotated Table Layouts, extracted with the native dictionary of
/// that file. Each critical check that passes is shown able to fail on the same data: a copy of the
/// native file is tampered with one change (an item of a context, a sub-category of a header) and the
/// check must fail, and pass again once the change is reverted. The embedded censuses apply (none exists
/// today, so a violation would be a failure).
/// </summary>
[Collection(Native44Collection.Name)]
public sealed class NativePlaneCRealDataTests(Native44Fixture fixture) : IDisposable
{
    private static readonly object CopyGate = new();
    private static string? _copy;

    public void Dispose() => SqliteConnection.ClearAllPools();

    private void RequireConverted()
        => Assert.True(fixture.Converted, "The 4.4 native conversion did not run although the data directory exists (is ACE installed?).");

    private string Layouts44()
    {
        RepoPaths.EnsureTableLayouts44DirectoryExists();
        RequireConverted();
        return RealLayoutRepositories.Extract("layouts-4.4-native-dictionary", RepoPaths.TableLayouts44Directory, fixture.OutputPath);
    }

    private string Layouts43()
    {
        RepoPaths.EnsureTableLayouts43DirectoryExists();
        RequireConverted();
        return RealLayoutRepositories.Extract("layouts-4.3-native-dictionary", RepoPaths.TableLayouts43Directory, fixture.OutputPath);
    }

    private static NativePlaneCResult Run(string nativePath, string layoutsPath)
    {
        using var native = Open(nativePath, readOnly: true);
        using var layouts = Open(layoutsPath, readOnly: true);
        return NativePlaneC.Run(native, layouts);
    }

    private static SqliteConnection Open(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static CheckResult Check(NativePlaneCResult result, string id) => result.Checks.Single(c => c.Id == id);

    private static string Describe(CheckResult check) =>
        $"{check.Id} {check.Status} examined={check.Examined} failed={check.Failed} " +
        string.Join("; ", check.Samples.Take(5).Select(s => $"{s.BusinessKey} [{s.Generated}] [{s.Reference}]"));

    // ------------------------------------------------------------------ the 4.4 layouts

    [DataFact]
    public void Layouts44_AgainstTheNative44Output_CriticalChecksPassOrAreSkippedForWantOfRules()
    {
        var result = Run(fixture.OutputPath, Layouts44());

        var dps = Check(result, "C-DPS-01");
        Assert.True(dps.Status == CheckStatus.Pass, Describe(dps));
        Assert.True(dps.Examined > 0, "C-DPS-01 compared no signature: " + Describe(dps));

        var mhr = Check(result, "C-MHR-01");
        Assert.True(mhr.Status == CheckStatus.Pass, Describe(mhr));
        Assert.True(mhr.Examined > 0, "C-MHR-01 compared no unit: " + Describe(mhr));

        // The 4.4 layouts contain no "==" rule: nothing to examine, so skipped, not passed.
        var equ = Check(result, "C-EQU-01");
        Assert.True(equ.Status == CheckStatus.Skipped, Describe(equ));
        Assert.Contains("0 checkable rules out of 0 total", equ.SkipReason);

        Assert.DoesNotContain(result.Checks, c => c.Layer == ValidationLayer.Critical && c.Status == CheckStatus.Fail);
        Assert.DoesNotContain(result.Divergences, d => d.Stale);
    }

    [DataFact]
    public void Layouts44_CoverageIsReported_EverySheetOfTheReleaseIsAccountedFor()
    {
        var result = Run(fixture.OutputPath, Layouts44());

        var cob = Check(result, "C-COB-01");
        var sample = Assert.Single(cob.Samples, s => s.BusinessKey == "release 4.4" && s.Generated!.Contains(" sheet(s): "));
        var match = Regex.Match(
            sample.Generated!,
            @"^(\d+) sheet\(s\): (\d+) with a unique table version at the release, (\d+) without a table version valid at the release, " +
            @"(\d+) with several table versions of the same code \(not compared\), (\d+) of a release not in the file \(not compared\); " +
            @"(\d+) table\(s\) compared, (\d+) signature\(s\)");
        Assert.True(match.Success, sample.Generated);
        var numbers = match.Groups.Cast<Group>().Skip(1).Select(g => long.Parse(g.Value, CultureInfo.InvariantCulture)).ToArray();
        Assert.True(numbers[0] > 0);
        Assert.Equal(numbers[0], numbers[1] + numbers[2] + numbers[3] + numbers[4]); // every sheet falls in one bucket.
        Assert.Equal(0, numbers[4]); // the release 4.4 is in the file.
        Assert.True(numbers[5] > 0);
    }

    [DataFact]
    public void Measured_20261006_4_4_Layouts_AgainstTheNative44File_Compare11427SignaturesIn215Tables()
    {
        // A measurement, not a property of the format: dated and scoped to DPM2 Database_v 4_4_20261006 and the
        // 4.4 table layouts that ship with it (measured 2026-11-25). It changes when either publication changes.
        var result = Run(fixture.OutputPath, Layouts44());

        var dps = Check(result, "C-DPS-01");
        Assert.Equal(11427, dps.Examined);
        var cob = Assert.Single(Check(result, "C-COB-01").Samples, s => s.BusinessKey == "release 4.4" && s.Generated!.Contains(" sheet(s): "));
        Assert.Contains("215 table(s) compared, 11427 signature(s)", cob.Generated);
    }

    // ------------------------------------------------------------------ C-EQU-01 on real data

    [DataFact]
    public void Layouts43_AgainstTheNative44File_EqualityRulesAreCheckedAndPass()
    {
        // The 4.4 file holds release 4.3, whose layouts carry "==" rules.
        var result = Run(fixture.OutputPath, Layouts43());

        var equ = Check(result, "C-EQU-01");
        Assert.True(equ.Status == CheckStatus.Pass, Describe(equ));
        Assert.True(equ.Examined > 0, "C-EQU-01 examined no rule: " + Describe(equ));
        Assert.Contains(" checkable (", Check(result, "C-EQU-02").Statement);

        var dps = Check(result, "C-DPS-01");
        Assert.True(dps.Status == CheckStatus.Pass && dps.Examined > 0, Describe(dps));
        var mhr = Check(result, "C-MHR-01");
        Assert.True(mhr.Status == CheckStatus.Pass && mhr.Examined > 0, Describe(mhr));
    }

    // ------------------------------------------------------------------ positive controls on a tampered copy

    private string TamperableCopy()
    {
        lock (CopyGate)
        {
            if (_copy is not null)
            {
                return _copy;
            }

            var directory = Path.Join(Path.GetTempPath(), $"native-plc-tamper_{Environment.ProcessId}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            _copy = Path.Join(directory, "native44-copy.db");
            File.Copy(fixture.OutputPath, _copy);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException)
                {
                    // Left for the OS temp cleanup.
                }
            };
            return _copy;
        }
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Exec(string path, params string[] statements)
    {
        using var connection = Open(path, readOnly: false);
        foreach (var sql in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    [DataFact]
    public void PositiveControl_AContextItemChanged_MakesCDps01FailAsContradicts_AndPassesAgainOnceReverted()
    {
        var layouts = Layouts44();
        var copy = TamperableCopy();
        Assert.Equal(CheckStatus.Pass, Check(Run(copy, layouts), "C-DPS-01").Status); // pristine copy.

        long contextId, propertyId, itemId, otherItemId;
        using (var native = Open(copy, readOnly: true))
        using (var layoutConnection = Open(layouts, readOnly: true))
        {
            var release = Scalar(native, "SELECT ReleaseID FROM Release WHERE Code = '4.4'");
            var codes = new List<string>();
            using (var command = layoutConnection.CreateCommand())
            {
                command.CommandText = "SELECT DISTINCT s.TableCode FROM LayoutSheet s JOIN LayoutFile f ON f.FileId = s.FileId WHERE f.ReleaseLabel = '4.4'";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    codes.Add("'" + reader.GetString(0).Replace("'", "''", StringComparison.Ordinal) + "'");
                }
            }

            // A non-default item of a context of a non-excluded cell of a 4.4 table version that has a layout sheet.
            using var pick = native.CreateCommand();
            pick.CommandText =
                $"""
                SELECT cc.ContextID, cc.PropertyID, cc.ItemID, ic.CategoryID
                FROM TableVersionCell tvc
                JOIN TableVersion tv ON tv.TableVID = tvc.TableVID
                JOIN VariableVersion vv ON vv.VariableVID = tvc.VariableVID
                JOIN ContextComposition cc ON cc.ContextID = vv.ContextID
                JOIN ItemCategory ic ON ic.ItemID = cc.ItemID AND ic.StartReleaseID <= {release}
                     AND (ic.EndReleaseID IS NULL OR ic.EndReleaseID > {release}) AND ic.IsDefaultItem = 0
                WHERE tv.StartReleaseID <= {release} AND (tv.EndReleaseID IS NULL OR tv.EndReleaseID > {release})
                  AND tvc.IsExcluded = 0 AND tv.Code IN ({string.Join(",", codes)})
                LIMIT 1
                """;
            using var row = pick.ExecuteReader();
            Assert.True(row.Read(), "no context item to tamper with");
            (contextId, propertyId, itemId) = (row.GetInt64(0), row.GetInt64(1), row.GetInt64(2));
            var categoryId = row.GetInt64(3);
            row.Close();
            otherItemId = Scalar(
                native,
                $"""
                SELECT ItemID FROM ItemCategory WHERE CategoryID = {categoryId} AND ItemID <> {itemId}
                AND StartReleaseID <= {release} AND (EndReleaseID IS NULL OR EndReleaseID > {release}) AND IsDefaultItem = 0 LIMIT 1
                """);
        }

        Exec(copy, $"UPDATE ContextComposition SET ItemID = {otherItemId} WHERE ContextID = {contextId} AND PropertyID = {propertyId}");
        try
        {
            var tampered = Check(Run(copy, layouts), "C-DPS-01");

            Assert.True(tampered.Status == CheckStatus.Fail, "the tampered context was not detected: " + Describe(tampered));
            Assert.True(tampered.Failed >= 1);
            Assert.Contains(tampered.Samples, s => s.Reference == "contradicts");
        }
        finally
        {
            Exec(copy, $"UPDATE ContextComposition SET ItemID = {itemId} WHERE ContextID = {contextId} AND PropertyID = {propertyId}");
        }

        var reverted = Check(Run(copy, layouts), "C-DPS-01");
        Assert.True(reverted.Status == CheckStatus.Pass, "the copy did not return to its pristine state: " + Describe(reverted));
    }

    [DataFact]
    public void PositiveControl_AHeaderSubCategoryChanged_MakesCMhr01Fail_AndPassesAgainOnceReverted()
    {
        var layouts = Layouts44();
        var copy = TamperableCopy();
        Assert.Equal(CheckStatus.Pass, Check(Run(copy, layouts), "C-MHR-01").Status); // pristine copy.

        string tableCode, axis, ordinate, metric;
        long headerVid, subCategoryVid, otherSubCategoryVid;
        using (var native = Open(copy, readOnly: true))
        using (var layoutConnection = Open(layouts, readOnly: true))
        {
            var release = Scalar(native, "SELECT ReleaseID FROM Release WHERE Code = '4.4'");

            // A layout metric with a hierarchy bracket whose header is found in the native file at 4.4.
            var units = new List<(string Table, string Axis, string Ordinate, string Metric)>();
            using (var command = layoutConnection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT DISTINCT s.TableCode, o.Axis, o.OrdinateCode, v.MemberCode
                    FROM LayoutValue v JOIN LayoutSheet s ON s.SheetId = v.SheetId JOIN LayoutFile f ON f.FileId = s.FileId
                    JOIN LayoutOrdinate o ON o.OrdinateId = v.OrdinateId
                    WHERE f.ReleaseLabel = '4.4' AND v.DomainCode = 'MET' AND v.HierarchyCode IS NOT NULL
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    units.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2).Trim(), reader.GetString(3)));
                }
            }

            Assert.NotEmpty(units);
            (tableCode, axis, ordinate, metric, headerVid, subCategoryVid) = FindHeader(native, release, units);
            otherSubCategoryVid = Scalar(
                native,
                $"""
                SELECT scv.SubCategoryVID FROM SubCategoryVersion scv JOIN SubCategory sc ON sc.SubCategoryID = scv.SubCategoryID
                WHERE sc.Code <> (SELECT sc2.Code FROM SubCategoryVersion v2 JOIN SubCategory sc2 ON sc2.SubCategoryID = v2.SubCategoryID
                                  WHERE v2.SubCategoryVID = {subCategoryVid})
                LIMIT 1
                """);
        }

        Exec(copy, $"UPDATE HeaderVersion SET SubCategoryVID = {otherSubCategoryVid} WHERE HeaderVID = {headerVid}");
        try
        {
            var tampered = Check(Run(copy, layouts), "C-MHR-01");

            Assert.True(tampered.Status == CheckStatus.Fail, "the tampered header was not detected: " + Describe(tampered));
            Assert.True(tampered.Failed >= 1);
            Assert.Contains(tampered.Samples, s => s.BusinessKey == $"{tableCode}\t4.4|{axis}|{ordinate}|{metric}");
        }
        finally
        {
            Exec(copy, $"UPDATE HeaderVersion SET SubCategoryVID = {subCategoryVid} WHERE HeaderVID = {headerVid}");
        }

        var reverted = Check(Run(copy, layouts), "C-MHR-01");
        Assert.True(reverted.Status == CheckStatus.Pass, "the copy did not return to its pristine state: " + Describe(reverted));
    }

    private static (string Table, string Axis, string Ordinate, string Metric, long HeaderVid, long SubCategoryVid) FindHeader(
        SqliteConnection native, long release, List<(string Table, string Axis, string Ordinate, string Metric)> units)
    {
        foreach (var (table, axis, ordinate, metric) in units)
        {
            using var command = native.CreateCommand();
            command.CommandText =
                $"""
                SELECT hv.HeaderVID, hv.SubCategoryVID
                FROM TableVersion tv
                JOIN TableVersionHeader tvh ON tvh.TableVID = tv.TableVID
                JOIN Header h ON h.HeaderID = tvh.HeaderID
                JOIN HeaderVersion hv ON hv.HeaderVID = tvh.HeaderVID
                JOIN ItemCategory ic ON ic.ItemID = hv.PropertyID AND ic.StartReleaseID <= {release}
                     AND (ic.EndReleaseID IS NULL OR ic.EndReleaseID > {release})
                WHERE tv.Code = $table AND tv.StartReleaseID <= {release} AND (tv.EndReleaseID IS NULL OR tv.EndReleaseID > {release})
                  AND h.IsKey = 0 AND h.Direction = $axis AND hv.Code = $ordinate AND ic.Code = $metric
                  AND hv.SubCategoryVID IS NOT NULL
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$table", table);
            command.Parameters.AddWithValue("$axis", axis);
            command.Parameters.AddWithValue("$ordinate", ordinate);
            command.Parameters.AddWithValue("$metric", metric);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return (table, axis, ordinate, metric, reader.GetInt64(0), reader.GetInt64(1));
            }
        }

        throw new InvalidOperationException("no layout metric with a hierarchy has a header with a sub-category at 4.4");
    }
}
