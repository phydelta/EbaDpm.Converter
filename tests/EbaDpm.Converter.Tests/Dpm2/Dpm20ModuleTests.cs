using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Tests.Dictionary;
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
/// <c>mModule</c> emits 52 rows, not 50 -- <c>P3_NONREM_DIS_DOCS</c> and <c>P3_REM_DIS_DOCS</c> are
/// left over (links to <c>PILLAR3</c> PDFs, with no table in <c>ModuleVersionComposition</c>, absent
/// from <c>EBA_4.2_Hotfix.db</c>). A NOMINAL test below explicitly checks that they are NOT emitted.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class Dpm20ModuleTests(Dpm20SkeletonFixture fixture)
{
    // ------------------------------------------------------------------
    // 1 - Row counts (critical layer: table coverage/structure).
    // ------------------------------------------------------------------

    [DataFact]
    public void MModule_Has50Rows() => Assert.Equal(50, CountRows("mModule"));

    [DataFact]
    public void MConceptualModule_Has50Rows() => Assert.Equal(50, CountRows("mConceptualModule"));

    [DataFact]
    public void MModuleBusinessTemplate_Has127Rows() => Assert.Equal(127, CountRows("mModuleBusinessTemplate"));

    // ------------------------------------------------------------------
    // 2 - The third filter: the two PILLAR3 document modules are NOT emitted.
    // It is the kind of filter that a future refactor might remove thinking it is redundant:
    // without it, the count would go from 50 to 52 without any other structural check noticing.
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

        // That the count would indeed be 52 without the third filter cannot be checked by this test
        // without re-reading the Access database (the filter was already applied when the .db was
        // generated) -- the evidence is in the comment of Dpm20ModuleLoader.
    }

    // ------------------------------------------------------------------
    // 3 - ModuleCode in UPPERCASE. Internal invariant over what was generated: NO ModuleCode
    // contains lowercase letters. And against the raw source (ModuleVersion.Code, not normalized):
    // only TWO codes change, REM_HR_Country and REM_HR_Institution.
    // ------------------------------------------------------------------

    [DataFact]
    public void MModule_ModuleCode_IsAlwaysUppercase_All50Rows()
    {
        var codes = QueryHelpers.Rows(fixture.GeneratedConnection, "SELECT \"ModuleCode\" FROM \"mModule\"", 1)
            .Select(r => r[0]!)
            .ToList();

        Assert.Equal(50, codes.Count);

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
        Assert.Equal(50, generatedCodes.Count);

        // Fresh, cheap read: only ModuleVersion (the rest of the large DPM 2.0 Access database is not needed).
        using var rawReader = new Dpm20AccessReader(RepoPaths.AccessDpm20DatabasePath, cutoffReleaseCode: RepoPaths.Cutoff42ReleaseCode);
        rawReader.Open();
        var rawModuleVersions = rawReader.ReadModuleVersions().ToList();

        // Those that survive the emission filter are exactly those that, in uppercase, are in the
        // generated set (DORA and the two isDocumentModule modules are left out as they do not appear there).
        var emittedRaw = rawModuleVersions
            .Where(mv => generatedCodes.Contains(mv.Code.ToUpperInvariant()))
            .ToList();
        Assert.Equal(50, emittedRaw.Count);

        var changedByUppercasing = emittedRaw
            .Where(mv => mv.Code != mv.Code.ToUpperInvariant())
            .Select(mv => mv.Code)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["REM_HR_Country", "REM_HR_Institution"], changedByUppercasing);
    }

    // ------------------------------------------------------------------
    // 4 - ModuleLabel = ModuleVersion.Name, verbatim, 50/50 against the reference. The candidate
    // "code with underscores as spaces" is NOT used as an assertion (it would only match 11/50,
    // e.g. IF_TM -> "IF Credit Institution Threshold Monitoring" is not "If Tm"): it stays only as
    // a note so the mistake is not repeated.
    // ------------------------------------------------------------------

    [DataFact]
    public void MModule_ModuleLabel_MatchesTheReference_50Of50_ByModuleCode()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var generatedByCode = QueryHelpers.Rows(fixture.GeneratedConnection, "SELECT \"ModuleCode\", \"ModuleLabel\" FROM \"mModule\"", 2)
            .ToDictionary(r => r[0]!, r => r[1], StringComparer.Ordinal);
        Assert.Equal(50, generatedByCode.Count);

        using var referenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);
        var referenceByCode = QueryHelpers.Rows(referenceConnection, "SELECT \"ModuleCode\", \"ModuleLabel\" FROM \"mModule\"", 2)
            .ToDictionary(r => r[0]!, r => r[1], StringComparer.Ordinal);
        Assert.Equal(50, referenceByCode.Count);

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
            $"{mismatches.Count}/50 ModuleLabel values differ from the reference; expected exactly {string.Join(", ", expected)}. Actual:\n" + string.Join("\n", mismatches));
    }

    // ------------------------------------------------------------------
    // 5 - XBRLSchemaRef, derived pattern, 50/50 against the reference. In DPM 1.0 this was NOT
    // derivable; here it is compared with no threshold, exactly as the reference has it.
    // ------------------------------------------------------------------

    [DataFact]
    public void MModule_XBRLSchemaRef_MatchesTheReference_50Of50_ByModuleCode()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var generatedByCode = QueryHelpers.Rows(fixture.GeneratedConnection, "SELECT \"ModuleCode\", \"XBRLSchemaRef\" FROM \"mModule\"", 2)
            .ToDictionary(r => r[0]!, r => r[1], StringComparer.Ordinal);
        Assert.Equal(50, generatedByCode.Count);

        using var referenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);
        var referenceByCode = QueryHelpers.Rows(referenceConnection, "SELECT \"ModuleCode\", \"XBRLSchemaRef\" FROM \"mModule\"", 2)
            .ToDictionary(r => r[0]!, r => r[1], StringComparer.Ordinal);
        Assert.Equal(50, referenceByCode.Count);

        var mismatches = new List<string>();
        foreach (var (code, referenceRef) in referenceByCode)
        {
            Assert.True(generatedByCode.TryGetValue(code, out var generatedRef), $"Reference ModuleCode '{code}' is missing from the generated output.");
            if (!string.Equals(generatedRef, referenceRef, StringComparison.Ordinal))
            {
                mismatches.Add($"{code}: generated='{generatedRef}' reference='{referenceRef}'");
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count}/50 XBRLSchemaRef values differ from the reference:\n" + string.Join("\n", mismatches));
    }

    // ------------------------------------------------------------------
    // 6 - Constant / source-less columns: AutogenerateRefs = 1, JsonBlob empty (not NULL),
    // DefaultFrequency and JSONSchemaRef NULL. Over the 50 rows.
    // ------------------------------------------------------------------

    [DataFact]
    public void MModule_ConstantColumns_AutogenerateRefsOne_JsonBlobEmpty_FrequencyAndJsonSchemaNull()
    {
        Assert.Equal(50, CountRows("mModule"));

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
    // reference: same ConceptualModuleID = ModuleID, same code, same label, 50 of 50.
    // ------------------------------------------------------------------

    [DataFact]
    public void MConceptualModule_IsAnExactMirrorOfMModule_50Of50_WithoutLookingAtTheReference()
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

        Assert.Equal(50, rows.Count);
        Assert.Equal(50, CountRows("mConceptualModule")); // no mConceptualModule row is left over

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

        Assert.True(mismatches.Count == 0, $"{mismatches.Count}/50 are not an exact mirror:\n" + string.Join("\n", mismatches));
    }

    // ------------------------------------------------------------------
    // 8 - mModuleBusinessTemplate.BusinessTemplateID ALWAYS points to Level 1 nodes of type
    // TableGroup, 127 of 127, over 111 distinct nodes. Internal invariant.
    // ------------------------------------------------------------------

    [DataFact]
    public void MModuleBusinessTemplate_BusinessTemplateID_AlwaysPointsToLevel1TableGroupNodes_127Of127()
    {
        var rows = QueryHelpers.Rows(
            fixture.GeneratedConnection,
            """
            SELECT bt."ModuleID", bt."BusinessTemplateID", t."TemplateOrTableType", t."Level"
            FROM "mModuleBusinessTemplate" bt
            JOIN "mTemplateOrTable" t ON t."TemplateOrTableID" = bt."BusinessTemplateID"
            """,
            4);

        Assert.Equal(127, rows.Count);

        var wrong = rows.Where(r => r[2] != "TableGroup" || r[3] != "1").ToList();
        Assert.True(
            wrong.Count == 0,
            $"{wrong.Count}/127 BusinessTemplateID values that are NOT Level 1 / TableGroup:\n"
            + string.Join("\n", wrong.Take(20).Select(r => $"ModuleID={r[0]} BusinessTemplateID={r[1]} Type={r[2]} Level={r[3]}")));

        var distinctNodes = rows.Select(r => r[1]).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(111, distinctNodes.Count);
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

        Assert.Equal(127, rows.Count);

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
