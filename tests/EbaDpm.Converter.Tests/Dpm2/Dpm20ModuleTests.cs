using EbaDpm.Converter.Tests.Dictionary;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Verification of <c>mModule</c>, <c>mConceptualModule</c> and <c>mModuleBusinessTemplate</c> for
/// the DPM 2.0 source. It reuses <see cref="Dpm20SkeletonFixture"/> (collection
/// <c>Dpm2Skeleton</c>): the same <c>--all</c> conversion already runs <c>Dpm20ModuleLoader.Load</c>,
/// so the large DPM 2.0 Access database does not need to be re-read for the generated content.
///
/// <b>The third module filter</b>: without excluding <c>[Module].isDocumentModule</c>,
/// <c>mModule</c> would emit two rows more -- <c>P3_NONREM_DIS_DOCS</c> and <c>P3_REM_DIS_DOCS</c>
/// are left over (links to <c>PILLAR3</c> PDFs, with no table in <c>ModuleVersionComposition</c>,
/// absent from the reference export). A test below checks on the Access source that exactly those two
/// exist and that they are NOT emitted.
///
/// The module count is DERIVED from the Access source (<see cref="AccessCensus"/>): the
/// <c>ModuleVersion</c> rows in force at the cutoff release, of a module that is not a document
/// module and whose framework is not DORA (docs/mapping-dpm2.md, "Modules").
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class Dpm20ModuleTests(Dpm20SkeletonFixture fixture)
{
    // ------------------------------------------------------------------
    // 1 - Row counts (critical layer: table coverage/structure).
    // ------------------------------------------------------------------

    private static string Accdb => RepoPaths.AccessDpm20DatabasePath;

    /// <summary>Asserts a figure derived from the Access source; the failure message says what and from where.</summary>
    private static void AssertDerived(string what, long expectedFromAccess, long actual)
        => Assert.True(
            expectedFromAccess == actual,
            $"{what}: expected {expectedFromAccess} (derived from '{RepoPaths.AccessDpm20DatabaseFileName}'), actual {actual}.");

    /// <summary>Asserts a figure derived from another generated table (a 1:1 mirror).</summary>
    private static void AssertMirrors(string what, long expectedFromOtherTable, long actual)
        => Assert.True(expectedFromOtherTable == actual, $"{what}: expected {expectedFromOtherTable} (derived from the generated mModule), actual {actual}.");

    private const string ModuleVersionJoin =
        "FROM ([ModuleVersion] mv INNER JOIN [Module] m ON m.[ModuleID] = mv.[ModuleID]) "
        + "INNER JOIN [Framework] f ON f.[FrameworkID] = m.[FrameworkID]";

    /// <summary>The "in force at the cutoff release" predicate over <c>mv</c>.</summary>
    private static string InForceAtCutoff()
        => AccessCensus.InForce(AccessCensus.ReleaseId(Accdb, RepoPaths.Cutoff42ReleaseCode), "mv");

    /// <summary>Code to <c>ModuleVID</c> of the module versions in force that are not DORA, split by <c>isDocumentModule</c>.</summary>
    private static Dictionary<string, int> ModuleVersionsInAccess(bool documentModules)
        => AccessCensus.IntByCode(
            Accdb,
            $"SELECT mv.[Code], mv.[ModuleVID] {ModuleVersionJoin} WHERE {InForceAtCutoff()} "
            + $"AND m.[isDocumentModule] {(documentModules ? "<>" : "=")} 0 AND f.[Code] <> 'DORA'");

    /// <summary>The expected <c>mModule</c> rows: in force, not a document module, not DORA.</summary>
    private static long ExpectedModuleRows() => ModuleVersionsInAccess(documentModules: false).Count;

    [DataFact]
    public void MModule_RowCount_IsTheModuleVersionsInForceExcludingDocumentModulesAndDora()
        => AssertDerived(
            "mModule rows (ModuleVersion in force, not a document module, framework not DORA)",
            ExpectedModuleRows(),
            CountRows("mModule"));

    [DataFact]
    public void MConceptualModule_RowCount_MirrorsMModule()
        => AssertMirrors("mConceptualModule rows (1:1 mirror of mModule)", CountRows("mModule"), CountRows("mConceptualModule"));

    [DataFact]
    public void MModuleBusinessTemplate_RowCount_MatchesThePinnedCensus()
        => ReleasePins.AssertPinned(
            ReleasePins.Dpm20Cutoff42,
            "mModuleBusinessTemplate rows",
            ReleasePins.Dpm20Cutoff42MModuleBusinessTemplateRows,
            CountRows("mModuleBusinessTemplate"));

    // ------------------------------------------------------------------
    // 2 - The third filter: the two PILLAR3 document modules are NOT emitted.
    // It is the kind of filter that a future refactor might remove thinking it is redundant:
    // without it, mModule would gain two rows without any other structural check noticing.
    // ------------------------------------------------------------------

    [DataFact]
    public void MModule_DoesNotEmitTheTwoDocumentModules_P3NonremDisDocs_And_P3RemDisDocs()
    {
        var codes = QueryHelpers.Rows(fixture.GeneratedConnection, "SELECT \"ModuleCode\" FROM \"mModule\"", 1)
            .Select(r => r[0]!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.True(codes.Count > 0, "mModule is empty: the check would be meaningless.");

        Assert.DoesNotContain("P3_NONREM_DIS_DOCS", codes);
        Assert.DoesNotContain("P3_REM_DIS_DOCS", codes);

    }

    /// <summary>
    /// Positive control of the third filter, on the Access source: the document modules in force are
    /// exactly the two named ones, and without the <c>isDocumentModule</c> filter the in-force,
    /// non-DORA module versions would be the derived <c>mModule</c> count plus those two.
    /// </summary>
    [DataFact]
    public void DocumentModulesInForce_AreExactlyTheTwoNamedOnes_AndWouldAddTwoRowsWithoutTheFilter()
    {
        var documentCodes = ModuleVersionsInAccess(documentModules: true).Keys
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["P3_NONREM_DIS_DOCS", "P3_REM_DIS_DOCS"], documentCodes);

        var withoutTheFilter = AccessCensus.Scalar(
            Accdb,
            $"SELECT COUNT(1) {ModuleVersionJoin} WHERE {InForceAtCutoff()} AND f.[Code] <> 'DORA'");
        AssertDerived(
            "ModuleVersion in force, framework not DORA, WITHOUT the isDocumentModule filter (expected mModule rows + 2 document modules)",
            ExpectedModuleRows() + 2,
            withoutTheFilter);
    }

    // ------------------------------------------------------------------
    // 3 - ModuleCode in UPPERCASE. Internal invariant over what was generated: NO ModuleCode
    // contains lowercase letters. And against the raw source (ModuleVersion.Code, not normalized):
    // the generated codes are exactly the upper-cased source codes, and only TWO source codes
    // change, REM_HR_Country and REM_HR_Institution.
    // ------------------------------------------------------------------

    [DataFact]
    public void MModule_ModuleCode_IsAlwaysUppercase()
    {
        var codes = QueryHelpers.Rows(fixture.GeneratedConnection, "SELECT \"ModuleCode\" FROM \"mModule\"", 1)
            .Select(r => r[0]!)
            .ToList();

        AssertDerived("mModule ModuleCode rows read", ExpectedModuleRows(), codes.Count);

        var notUpper = codes.Where(c => c != c.ToUpperInvariant()).ToList();
        Assert.True(notUpper.Count == 0, $"ModuleCode with lowercase letters: {string.Join(", ", notUpper)}");
    }

    [DataFact]
    public void MModule_ModuleCode_OnlyTwoCodesChangeWhenNormalized_RemHrCountry_And_RemHrInstitution()
    {
        RepoPaths.EnsureAccessDpm20DatabaseExists();

        var generatedCodes = QueryHelpers.Rows(fixture.GeneratedConnection, "SELECT \"ModuleCode\" FROM \"mModule\"", 1)
            .Select(r => r[0]!)
            .ToHashSet(StringComparer.Ordinal);

        // Raw source codes (ModuleVersion.Code, not normalized) of the emitted population, read straight
        // from Access (no production reader).
        var rawCodes = ModuleVersionsInAccess(documentModules: false).Keys.ToList();
        AssertDerived("mModule distinct ModuleCode", rawCodes.Count, generatedCodes.Count);

        var expectedGenerated = rawCodes.Select(c => c.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        Assert.True(
            expectedGenerated.SetEquals(generatedCodes),
            $"ModuleCode set differs from the upper-cased source codes. Only in source: {string.Join(", ", expectedGenerated.Except(generatedCodes))}. "
            + $"Only in output: {string.Join(", ", generatedCodes.Except(expectedGenerated))}.");

        var changedByUppercasing = rawCodes
            .Where(c => c != c.ToUpperInvariant())
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["REM_HR_Country", "REM_HR_Institution"], changedByUppercasing);
    }

    // ------------------------------------------------------------------
    // 4 - ModuleLabel = ModuleVersion.Name, verbatim, every row against the reference. The candidate
    // "code with underscores as spaces" is NOT used as an assertion (it would match only a minority,
    // e.g. IF_TM -> "IF Credit Institution Threshold Monitoring" is not "If Tm"): it stays only as
    // a note so the mistake is not repeated.
    // ------------------------------------------------------------------

    [DataFact]
    public void MModule_ModuleLabel_MatchesTheReference_ByModuleCode()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var generatedByCode = QueryHelpers.Rows(fixture.GeneratedConnection, "SELECT \"ModuleCode\", \"ModuleLabel\" FROM \"mModule\"", 2)
            .ToDictionary(r => r[0]!, r => r[1], StringComparer.Ordinal);
        AssertDerived("generated mModule rows", ExpectedModuleRows(), generatedByCode.Count);

        using var referenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);
        var referenceByCode = QueryHelpers.Rows(referenceConnection, "SELECT \"ModuleCode\", \"ModuleLabel\" FROM \"mModule\"", 2)
            .ToDictionary(r => r[0]!, r => r[1], StringComparer.Ordinal);
        ReleasePins.AssertPinned(ReleasePins.Reference42, "reference mModule rows", ReleasePins.Reference42MModuleRows, referenceByCode.Count);

        var missingInGenerated = referenceByCode.Keys.Except(generatedByCode.Keys).ToList();
        Assert.True(missingInGenerated.Count == 0, $"Reference ModuleCode values missing from the generated output: {string.Join(", ", missingInGenerated)}");

        var mismatches = new List<string>();
        foreach (var (code, referenceLabel) in referenceByCode)
        {
            var generatedLabel = generatedByCode[code];
            if (!string.Equals(generatedLabel, referenceLabel, StringComparison.Ordinal))
            {
                mismatches.Add($"{code}: generated='{generatedLabel}' reference='{referenceLabel}'");
            }
        }

        // The single expected difference, named by business key: CODIS. The EBA corrected the label in
        // "DPM2 Database_v 4_2_1.accdb" ("Common disclosures"); the reference export keeps the typo
        // ("Commun disclosures"). Both sides are asserted exactly, so if either changes this fails
        // (a stale exception must fail). Everything else must match the reference exactly.
        var expected = new[] { "CODIS: generated='Common disclosures' reference='Commun disclosures'" };
        Assert.True(
            mismatches.SequenceEqual(expected, StringComparer.Ordinal),
            $"{mismatches.Count}/{referenceByCode.Count} ModuleLabel values differ from the reference; expected exactly {string.Join(", ", expected)}. Actual:\n" + string.Join("\n", mismatches));
    }

    // ------------------------------------------------------------------
    // 5 - XBRLSchemaRef, derived pattern, every row against the reference. In DPM 1.0 this was NOT
    // derivable; here it is compared with no threshold, exactly as the reference has it.
    // ------------------------------------------------------------------

    [DataFact]
    public void MModule_XBRLSchemaRef_MatchesTheReference_ByModuleCode()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var generatedByCode = QueryHelpers.Rows(fixture.GeneratedConnection, "SELECT \"ModuleCode\", \"XBRLSchemaRef\" FROM \"mModule\"", 2)
            .ToDictionary(r => r[0]!, r => r[1], StringComparer.Ordinal);
        AssertDerived("generated mModule rows", ExpectedModuleRows(), generatedByCode.Count);

        using var referenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);
        var referenceByCode = QueryHelpers.Rows(referenceConnection, "SELECT \"ModuleCode\", \"XBRLSchemaRef\" FROM \"mModule\"", 2)
            .ToDictionary(r => r[0]!, r => r[1], StringComparer.Ordinal);
        ReleasePins.AssertPinned(ReleasePins.Reference42, "reference mModule rows", ReleasePins.Reference42MModuleRows, referenceByCode.Count);

        var mismatches = new List<string>();
        foreach (var (code, referenceRef) in referenceByCode)
        {
            Assert.True(generatedByCode.TryGetValue(code, out var generatedRef), $"Reference ModuleCode '{code}' is missing from the generated output.");
            if (!string.Equals(generatedRef, referenceRef, StringComparison.Ordinal))
            {
                mismatches.Add($"{code}: generated='{generatedRef}' reference='{referenceRef}'");
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count}/{referenceByCode.Count} XBRLSchemaRef values differ from the reference:\n" + string.Join("\n", mismatches));
    }

    // ------------------------------------------------------------------
    // 6 - Constant / source-less columns: AutogenerateRefs = 1, JsonBlob empty (not NULL),
    // DefaultFrequency and JSONSchemaRef NULL. Over all the rows.
    // ------------------------------------------------------------------

    [DataFact]
    public void MModule_ConstantColumns_AutogenerateRefsOne_JsonBlobEmpty_FrequencyAndJsonSchemaNull()
    {
        AssertDerived("mModule rows checked", ExpectedModuleRows(), CountRows("mModule"));

        var notAutogenerate = ScalarLong("SELECT COUNT(*) FROM \"mModule\" WHERE \"AutogenerateRefs\" IS NULL OR \"AutogenerateRefs\" <> 1");
        Assert.Equal(0, notAutogenerate);

        // JsonBlob: not NULL, and of length 0 -- valid whether it was stored as an empty BLOB or as
        // an empty string (SQLite column affinity does not force a specific representation; what
        // matters is "empty, not NULL").
        var jsonBlobNotEmpty = ScalarLong("SELECT COUNT(*) FROM \"mModule\" WHERE \"JsonBlob\" IS NULL OR length(\"JsonBlob\") <> 0");
        Assert.Equal(0, jsonBlobNotEmpty);

        var defaultFrequencyNotNull = ScalarLong("SELECT COUNT(*) FROM \"mModule\" WHERE \"DefaultFrequency\" IS NOT NULL");
        Assert.Equal(0, defaultFrequencyNotNull);

        var jsonSchemaRefNotNull = ScalarLong("SELECT COUNT(*) FROM \"mModule\" WHERE \"JSONSchemaRef\" IS NOT NULL");
        Assert.Equal(0, jsonSchemaRefNotNull);
    }

    // ------------------------------------------------------------------
    // 7 - mConceptualModule: 1:1 mirror of mModule. Internal invariant, WITHOUT looking at the
    // reference: same ConceptualModuleID = ModuleID, same code, same label, for every row.
    // ------------------------------------------------------------------

    [DataFact]
    public void MConceptualModule_IsAnExactMirrorOfMModule_WithoutLookingAtTheReference()
    {
        var rows = QueryHelpers.Rows(
            fixture.GeneratedConnection,
            """
            SELECT m."ModuleID", m."ModuleCode", m."ModuleLabel", m."ConceptualModuleID",
                   cm."ConceptualModuleID", cm."ConceptualModuleCode", cm."ConceptualModuleLabel"
            FROM "mModule" m
            JOIN "mConceptualModule" cm ON cm."ConceptualModuleID" = m."ConceptualModuleID"
            """,
            7);

        AssertDerived("mModule rows joined to mConceptualModule", ExpectedModuleRows(), rows.Count);
        AssertDerived("mConceptualModule rows (no row is left over)", ExpectedModuleRows(), CountRows("mConceptualModule"));

        var mismatches = new List<string>();
        foreach (var row in rows)
        {
            var moduleId = row[0];
            var moduleCode = row[1];
            var moduleLabel = row[2];
            var conceptualModuleIdViaFk = row[3];
            var conceptualModuleId = row[4];
            var conceptualModuleCode = row[5];
            var conceptualModuleLabel = row[6];

            if (!string.Equals(moduleId, conceptualModuleIdViaFk, StringComparison.Ordinal)
                || !string.Equals(moduleId, conceptualModuleId, StringComparison.Ordinal)
                || !string.Equals(moduleCode, conceptualModuleCode, StringComparison.Ordinal)
                || !string.Equals(moduleLabel, conceptualModuleLabel, StringComparison.Ordinal))
            {
                mismatches.Add(
                    $"ModuleID={moduleId}: mModule(Code={moduleCode},Label={moduleLabel},ConceptualModuleID={conceptualModuleIdViaFk}) "
                    + $"vs mConceptualModule(ID={conceptualModuleId},Code={conceptualModuleCode},Label={conceptualModuleLabel})");
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count}/{rows.Count} are not an exact mirror:\n" + string.Join("\n", mismatches));
    }

    // ------------------------------------------------------------------
    // 8 - mModuleBusinessTemplate.BusinessTemplateID ALWAYS points to Level 1 nodes of type
    // TableGroup, for every row, over a pinned number of distinct nodes. Internal invariant.
    // ------------------------------------------------------------------

    [DataFact]
    public void MModuleBusinessTemplate_BusinessTemplateID_AlwaysPointsToLevel1TableGroupNodes()
    {
        var rows = QueryHelpers.Rows(
            fixture.GeneratedConnection,
            """
            SELECT bt."ModuleID", bt."BusinessTemplateID", t."TemplateOrTableType", t."Level"
            FROM "mModuleBusinessTemplate" bt
            JOIN "mTemplateOrTable" t ON t."TemplateOrTableID" = bt."BusinessTemplateID"
            """,
            4);

        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Cutoff42,
            "mModuleBusinessTemplate rows joined to mTemplateOrTable",
            ReleasePins.Dpm20Cutoff42MModuleBusinessTemplateRows,
            rows.Count);

        var wrong = rows.Where(r => r[2] != "TableGroup" || r[3] != "1").ToList();
        Assert.True(
            wrong.Count == 0,
            $"{wrong.Count}/{rows.Count} BusinessTemplateID values that are NOT Level 1 / TableGroup:\n"
            + string.Join("\n", wrong.Take(20).Select(r => $"ModuleID={r[0]} BusinessTemplateID={r[1]} Type={r[2]} Level={r[3]}")));

        var distinctNodes = rows.Select(r => r[1]).ToHashSet(StringComparer.Ordinal);
        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Cutoff42,
            "distinct BusinessTemplateID nodes of mModuleBusinessTemplate",
            ReleasePins.Dpm20Cutoff42BusinessTemplateDistinctNodes,
            distinctNodes.Count);
    }

    // ------------------------------------------------------------------
    // 9 - The Order of mModuleBusinessTemplate is a DECLARED CHOICE, NOT derived. This test is
    // about INTERNAL COHERENCE only -- contiguous 0-based range within each module, exactly one row
    // with Order = 0 per module -- and compares nothing against the reference.
    // ------------------------------------------------------------------

    [DataFact]
    public void MModuleBusinessTemplate_Order_IsAContiguousZeroBasedRangePerModule_WithASingleZeroPerModule()
    {
        var rows = QueryHelpers.Rows(
            fixture.GeneratedConnection,
            "SELECT \"ModuleID\", \"Order\" FROM \"mModuleBusinessTemplate\" ORDER BY \"ModuleID\", \"Order\"",
            2);

        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Cutoff42,
            "mModuleBusinessTemplate rows",
            ReleasePins.Dpm20Cutoff42MModuleBusinessTemplateRows,
            rows.Count);

        var byModule = rows
            .GroupBy(r => r[0]!)
            .ToList();

        Assert.True(byModule.Count > 0, "There is no module in mModuleBusinessTemplate: the check would be empty.");

        var failures = new List<string>();
        foreach (var group in byModule)
        {
            var orders = group.Select(r => int.Parse(r[1]!)).OrderBy(o => o).ToList();
            var expected = Enumerable.Range(0, orders.Count).ToList();

            if (!orders.SequenceEqual(expected))
            {
                failures.Add($"ModuleID={group.Key}: Order=[{string.Join(",", orders)}], a contiguous 0-based range [{string.Join(",", expected)}] was expected");
                continue;
            }

            var zeroCount = orders.Count(o => o == 0);
            if (zeroCount != 1)
            {
                failures.Add($"ModuleID={group.Key}: {zeroCount} rows with Order=0 (exactly 1 expected)");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count}/{byModule.Count} modules violate the contiguous 0-based range with a single Order=0:\n" + string.Join("\n", failures));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private long CountRows(string table) => ScalarLong($"SELECT COUNT(*) FROM \"{table}\"");

    private long ScalarLong(string sql) => QueryHelpers.Scalar(fixture.GeneratedConnection, sql);

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
