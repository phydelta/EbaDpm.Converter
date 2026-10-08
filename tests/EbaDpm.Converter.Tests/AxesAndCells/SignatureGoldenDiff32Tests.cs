using System.Text.RegularExpressions;
using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.AxesAndCells;

/// <summary>
/// Golden-level comparison of the data point signatures against <c>EBA_3.2_phase_1.db</c>, the
/// PRIMARY reference.
///
/// <c>DatapointSignature</c> (the variant carrying database IDs) is NEVER compared literally
/// against the reference, because its IDs belong to another database. It is compared here
/// NORMALISING the numeric content of each <c>*[...]</c> bracket: what must be equal is the
/// STRUCTURE (number of segments), not the value of the IDs. <c>DPS</c> (XBRL codes), in
/// contrast, IS compared literally at 100%, because XBRL codes are comparable across databases.
///
/// Critical acceptance layer.
/// </summary>
[Collection("AxesAndCells")]
[Trait("Tier", "RealData")]
public sealed class SignatureGoldenDiff32Tests
{
    private static readonly HashSet<string> TargetTaxonomies = new(StringComparer.Ordinal)
    {
        "ae_3.2", "corep_3.2", "gsii_3.2", "if_3.2",
    };

    private readonly AxisAndCellFixture _fixture;

    public SignatureGoldenDiff32Tests(AxisAndCellFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>Replaces ANY digit sequence INSIDE a <c>*[...]</c> bracket with <c>#</c>.</summary>
    private static string NormalizeBracketNumerics(string signature) =>
        Regex.Replace(signature, @"\*\[[^\]]*\]", m => Regex.Replace(m.Value, @"\d+", "#"));

    private sealed record MatchedCellPair(int GeneratedCellId, int ReferenceCellId, string PositionKey);

    private List<MatchedCellPair> MatchCellsByPosition()
    {
        var tableIdsGenerated = BusinessKeySupport.BuildTableIdsByBusinessKey(_fixture.GeneratedConnection, TargetTaxonomies);
        var tableIdsReference = BusinessKeySupport.BuildTableIdsByBusinessKey(_fixture.Reference32Connection, TargetTaxonomies);

        var generatedIndex = BusinessKeySupport.BuildClosedCellPositionIndex(_fixture.GeneratedConnection, tableIdsGenerated.Values.ToHashSet());
        var referenceIndex = BusinessKeySupport.BuildClosedCellPositionIndex(_fixture.Reference32Connection, tableIdsReference.Values.ToHashSet());

        var result = new List<MatchedCellPair>();

        foreach (var (tableKey, refTableId) in tableIdsReference)
        {
            if (!tableIdsGenerated.TryGetValue(tableKey, out var genTableId))
            {
                continue; // checked elsewhere (table coverage)
            }

            foreach (var ((tableId, positionKey), refCellId) in referenceIndex)
            {
                if (tableId != refTableId)
                {
                    continue;
                }

                if (generatedIndex.TryGetValue((genTableId, positionKey), out var genCellId))
                {
                    result.Add(new MatchedCellPair(genCellId, refCellId, $"{tableKey.Taxonomy}/{tableKey.TableCode}/{positionKey}"));
                }
            }
        }

        return result;
    }

    // ------------------------------------------------------------------
    // DPS: 100% LITERAL against 3.2, over the UNSHADED cells. Matching is by position over
    // closed axes (NOT by BusinessCode: the open-axis convention differs between 3.2 and the
    // generated output).
    //
    // Over the FOUR 3.2 taxonomies (1:1 coverage with the Access), COUNT(*) of "mTableCell" WHERE
    // "IsShaded"=0 in the 3.2 reference is EXACTLY 69,357, and position matching finds all 69,357
    // without any collision (matched == unshaded on both sides).
    // ------------------------------------------------------------------

    [DataFact]
    public void Dps_MatchesReference32_Literally_ForAllUnshadedCells()
    {
        var matched = MatchCellsByPosition();
        Assert.True(matched.Count > 60000, $"Only {matched.Count} cells were matched: position matching failed wholesale.");

        var referenceShadedById = QueryHelpers.Rows(_fixture.Reference32Connection, "SELECT \"CellID\", \"IsShaded\" FROM \"mTableCell\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]);
        var referenceDpsById = QueryHelpers.Rows(_fixture.Reference32Connection, "SELECT \"CellID\", \"DPS\" FROM \"mTableCell\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]);
        var generatedDpsById = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"CellID\", \"DPS\" FROM \"mTableCell\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]);

        var compared = 0;
        var failures = new List<string>();

        foreach (var pair in matched)
        {
            if (referenceShadedById.GetValueOrDefault(pair.ReferenceCellId) != "0")
            {
                continue; // only cells NOT shaded in the reference (DPS is NULL otherwise)
            }

            compared++;
            var refDps = referenceDpsById.GetValueOrDefault(pair.ReferenceCellId);
            var genDps = generatedDpsById.GetValueOrDefault(pair.GeneratedCellId);

            if (!string.Equals(refDps, genDps, StringComparison.Ordinal))
            {
                failures.Add($"{pair.PositionKey}: generated='{genDps}' reference='{refDps}'");
            }
        }

        Assert.Equal(69357, compared);
        Assert.True(
            failures.Count == 0,
            $"{failures.Count}/{compared} unshaded cells with a DPS different from the 3.2 reference (literal, required at 100%):\n" + string.Join("\n", failures.Take(30)));
    }

    // ------------------------------------------------------------------
    // DatapointSignature: NORMALISING the numeric content of the brackets, also at 100% over the
    // same unshaded cells.
    // ------------------------------------------------------------------

    [DataFact]
    public void DatapointSignature_MatchesReference32_NormalizingBracketNumericContent_ForAllUnshadedCells()
    {
        var matched = MatchCellsByPosition();
        Assert.True(matched.Count > 60000);

        var referenceShadedById = QueryHelpers.Rows(_fixture.Reference32Connection, "SELECT \"CellID\", \"IsShaded\" FROM \"mTableCell\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]);
        var referenceDpsById = QueryHelpers.Rows(_fixture.Reference32Connection, "SELECT \"CellID\", \"DatapointSignature\" FROM \"mTableCell\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]);
        var generatedDpsById = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"CellID\", \"DatapointSignature\" FROM \"mTableCell\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]);

        var compared = 0;
        var withBracket = 0;
        var failures = new List<string>();

        foreach (var pair in matched)
        {
            if (referenceShadedById.GetValueOrDefault(pair.ReferenceCellId) != "0")
            {
                continue;
            }

            compared++;
            var refSignature = referenceDpsById.GetValueOrDefault(pair.ReferenceCellId);
            var genSignature = generatedDpsById.GetValueOrDefault(pair.GeneratedCellId);

            if (refSignature is null || genSignature is null)
            {
                failures.Add($"{pair.PositionKey}: one of the two signatures is NULL (generated='{genSignature}' reference='{refSignature}')");
                continue;
            }

            if (refSignature.Contains("*[", StringComparison.Ordinal) || genSignature.Contains("*[", StringComparison.Ordinal))
            {
                withBracket++;
            }

            var refNormalized = NormalizeBracketNumerics(refSignature);
            var genNormalized = NormalizeBracketNumerics(genSignature);

            if (!string.Equals(refNormalized, genNormalized, StringComparison.Ordinal))
            {
                failures.Add($"{pair.PositionKey}: generated(normalised)='{genNormalized}' reference(normalised)='{refNormalized}' (generated literal='{genSignature}' reference literal='{refSignature}')");
            }
        }

        Assert.Equal(69357, compared);
        Assert.True(withBracket > 0, "No cell with a bracket was compared: the normalisation would not cover its most delicate case.");
        Assert.True(
            failures.Count == 0,
            $"{failures.Count}/{compared} unshaded cells with a DatapointSignature different from the 3.2 reference after normalising the numeric content of the bracket (required at 100%):\n" + string.Join("\n", failures.Take(30)));
    }

    // ------------------------------------------------------------------
    // Census of the THREE-part bracket (starting member) on the REFERENCES themselves, not on the
    // generated output: 12,031/69,357 in 3.2, 0 in 4.0, 0 in 4.2 (the 4.x references never
    // populate HierarchyStartingMemberID). Regression guard for the reference data: if these
    // figures change, the data files were updated and the signature rules must be reviewed
    // before going on.
    // ------------------------------------------------------------------

    private static (int WithSignature, int ThreePartBracket) CensusThreePartBracket(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var rows = QueryHelpers.Rows(connection, "SELECT \"DPS\" FROM \"mTableCell\" WHERE \"DPS\" IS NOT NULL", 1);
        var threePart = rows.Count(r => Regex.IsMatch(r[0]!, @"\*\[[^\];]*;[^\];]*;[^\]]*\]"));
        return (rows.Count, threePart);
    }

    [DataFact]
    public void ThreePartBracket_Census_Reference32_Is12031Of69357()
    {
        var (withSignature, threePart) = CensusThreePartBracket(_fixture.Reference32Connection);
        Assert.Equal(69357, withSignature);
        Assert.Equal(12031, threePart);
    }

    [DataFact]
    public void ThreePartBracket_Census_Reference40_IsZero()
    {
        var (_, threePart) = CensusThreePartBracket(_fixture.Reference40Connection);
        Assert.Equal(0, threePart);
    }

    [DataFact]
    public void ThreePartBracket_Census_Reference42_IsZero()
    {
        var (_, threePart) = CensusThreePartBracket(_fixture.Reference42Connection);
        Assert.Equal(0, threePart);
    }
}
