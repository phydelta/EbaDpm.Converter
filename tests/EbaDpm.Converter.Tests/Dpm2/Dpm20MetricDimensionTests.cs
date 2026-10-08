using System.Text.RegularExpressions;
using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// <c>mMetric</c> and <c>mDimension</c>, the two tables that close the DPM 2.0 dictionary: in
/// particular the naming law that decides the string type, and the fact that the property code is
/// also versioned (window crossing). It reuses <see cref="Dpm20SkeletonFixture"/> (collection
/// <c>Dpm2Skeleton</c>): the same <c>--all</c> conversion already loads the six dictionary tables,
/// so the large DPM 2.0 Access database does not need to be re-read.
///
/// The count figures (2,107 <c>mMetric</c>, 1,100 <c>mDimension</c>) are the ones MEASURED over the
/// real <c>.db</c>, as they come out of <see cref="Dpm20DictionaryLoaderTests"/>.
///
/// <c>mDimension</c> has <b>1,100</b> rows: the row of the <c>MET</c> dimension
/// (<c>DimensionID</c> 9999) is included, because without it <c>mOrdinateCategorisation</c> would
/// point to a non-existent dimension -- an inconsistency that must not exist
/// (<c>Dpm20DictionaryLoader.WriteMetDimension</c>). The expected figure reflects that the missing
/// row was real; no comparison was relaxed.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class Dpm20MetricDimensionTests(Dpm20SkeletonFixture fixture)
{
    // ------------------------------------------------------------------
    // Counts
    // ------------------------------------------------------------------

    [DataFact]
    public void MMetric_Has2107Rows() => Assert.Equal(2107, CountRows("mMetric"));

    [DataFact]
    public void MDimension_Has1100Rows() => Assert.Equal(1100, CountRows("mDimension"));

    /// <summary>
    /// <c>mMetric</c> is a 1:1 mirror of the members of the <c>MET</c> domain: every row has a
    /// <c>CorrespondingMemberID</c>, that member exists and belongs to the <c>MET</c> domain, and
    /// none of the 2,107 <c>MET</c> members is missing or left over.
    /// </summary>
    [DataFact]
    public void MMetric_IsA1To1MirrorOfTheMembersOfTheMetDomain()
    {
        Assert.Equal(0, ScalarLong("SELECT COUNT(*) FROM mMetric WHERE CorrespondingMemberID IS NULL"));

        var metricMemberIds = ReadLongColumn(
            "SELECT CorrespondingMemberID FROM mMetric").ToHashSet();
        Assert.Equal(2107, metricMemberIds.Count); // no duplicates: 2,107 distinct for 2,107 rows

        var metMemberIds = ReadLongColumn(
            "SELECT m.MemberID FROM mMember m JOIN mDomain d ON m.DomainID = d.DomainID WHERE d.DomainCode = 'MET'")
            .ToHashSet();
        Assert.Equal(2107, metMemberIds.Count);

        // None left over, none missing: the two sets are THE SAME.
        Assert.Empty(metricMemberIds.Except(metMemberIds));
        Assert.Empty(metMemberIds.Except(metricMemberIds));
    }

    // ------------------------------------------------------------------
    // mMetric.DataType, the string type (naming law)
    // ------------------------------------------------------------------

    /// <summary>
    /// Against the reference: 0 discrepancies over the 2,104 common codes. It is the strong test of
    /// the string-type rule: if someone "restores" the direct translation <c>s</c> to
    /// <c>NotEmptyString</c>, <c>es</c> to <c>String</c> (false, 138 measured discrepancies), this
    /// test catches it.
    /// </summary>
    [DataFact]
    public void MMetric_DataType_ZeroDiscrepancies_AgainstTheCommonReferenceCodes()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var generated = ReadDataTypeByMemberCode(fixture.GeneratedConnection);
        using var referenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);
        var reference = ReadDataTypeByMemberCode(referenceConnection);

        var common = generated.Keys.Intersect(reference.Keys, StringComparer.Ordinal).ToList();
        Assert.Equal(2104, common.Count);

        var mismatches = common
            .Where(code => generated[code] != reference[code])
            .Select(code => $"{code}: generated={generated[code]}, reference={reference[code]}")
            .ToList();

        Assert.True(mismatches.Count == 0, "DataType discrepancies: " + string.Join(" | ", mismatches));
    }

    /// <summary>
    /// GUARD AGAINST THE OLD (false) RULE: the discriminator of the string type is NOT the raw
    /// <c>s</c>/<c>es</c> code of the source, it is the naming law of the property. Two named
    /// counterexamples, read directly from the source Access database, which the direct translation
    /// would classify THE WRONG WAY ROUND:
    /// <list type="bullet">
    /// <item><c>si3</c>: source <c>DataType.Code='s'</c>, matches the naming law -> target
    /// <c>String</c>. The old rule (fixed <c>s</c> to <c>NotEmptyString</c>) would give <c>NotEmptyString</c>.</item>
    /// <item><c>qANS</c>: source <c>DataType.Code='es'</c>, does NOT match the naming law -> target
    /// <c>NotEmptyString</c>. The old rule (fixed <c>es</c> to <c>String</c>) would give <c>String</c>.</item>
    /// </list>
    /// If someone one day "restores" the direct translation because it looks more logical when
    /// reading the source column name, this test fails with the two values inverted.
    /// </summary>
    [DataFact]
    public void MMetric_DataType_ForStrings_IsDecidedByTheNamingLaw_NotByTheRawSourceCode()
    {
        RepoPaths.EnsureAccessDpm20DatabaseExists();

        using var reader = new Dpm20AccessReader(RepoPaths.AccessDpm20DatabasePath, cutoffReleaseCode: RepoPaths.Cutoff42ReleaseCode);
        reader.Open();

        var dataTypeCodeById = reader.ReadDataTypes().ToDictionary(dt => dt.DataTypeId, dt => dt.Code);
        var dataTypeCodeByPropertyId = new Dictionary<int, string>();
        foreach (var property in reader.ReadProperties())
        {
            if (property.DataTypeId is { } dataTypeId && dataTypeCodeById.TryGetValue(dataTypeId, out var code))
            {
                dataTypeCodeByPropertyId[property.PropertyId] = code;
            }
        }

        var prCategoryId = reader.ReadCategories().Single(c => c.Code == "_PR").CategoryId;
        var propertyIdByCode = reader.ReadItemCategories()
            .Where(ic => ic.CategoryId == prCategoryId)
            .ToDictionary(ic => ic.Code, ic => ic.ItemId, StringComparer.Ordinal);

        // Confirms the premise of the counterexample: the raw codes in the source are the ones the
        // test says (if this fails, the Access database has changed and the counterexample has to be
        // redone, not relaxed).
        Assert.Equal("s", dataTypeCodeByPropertyId[propertyIdByCode["si3"]]);
        Assert.Equal("es", dataTypeCodeByPropertyId[propertyIdByCode["qANS"]]);

        Assert.Equal("String", DataTypeOfMetric(fixture.GeneratedConnection, "si3"));
        Assert.Equal("NotEmptyString", DataTypeOfMetric(fixture.GeneratedConnection, "qANS"));
    }

    private static string? DataTypeOfMetric(SqliteConnection connection, string memberCode)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT mt.DataType FROM mMetric mt JOIN mMember m ON mt.CorrespondingMemberID = m.MemberID WHERE m.MemberCode = $c";
        command.Parameters.AddWithValue("$c", memberCode);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : (string)value;
    }

    // ------------------------------------------------------------------
    // mDimension
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>DimensionXBRLCode</c> = <c>eba_dim_&lt;release&gt;:&lt;code&gt;</c> -- WITH a suffix,
    /// unlike <c>mDomain</c>/<c>mMember</c>. **Single exception, named by business key**:
    /// <c>MET</c> (<c>DimensionID</c> 9999) carries <c>DimensionXBRLCode = "MET"</c>, WITHOUT a
    /// prefix -- so the remaining 1,099 rows DO follow the format, and so does the reference
    /// (measured: <c>EBA_4.2_Hotfix.db</c> has <c>MET</c> -> <c>MET</c>, identical). Against the
    /// reference it matches by business key: the 1,089 common codes (1,088 + <c>MET</c>) each have
    /// at least one row whose <c>DimensionXBRLCode</c> matches EXACTLY the reference's (domain
    /// duplicates, see the next test, contribute more than one value per code).
    /// </summary>
    [DataFact]
    public void MDimension_DimensionXBRLCode_CarriesTheReleaseSuffix_AndMatchesByBusinessKey()
    {
        var pattern = new Regex(@"^eba_dim_[^:]+:.+$", RegexOptions.Compiled);
        var allRows = ReadCodeXbrlPairs(fixture.GeneratedConnection);
        Assert.Equal(1100, allRows.Count);

        // The ONLY exception to the suffixed format is MET, named by code -- not a general
        // relaxation of the pattern. Any other row without a suffix would be a real bug.
        var withoutSuffix = allRows.Where(r => r.Code != "MET" && !pattern.IsMatch(r.XbrlCode)).ToList();
        Assert.True(
            withoutSuffix.Count == 0,
            "Dimension codes (other than MET) without the eba_dim_<release> suffix: " + string.Join(", ", withoutSuffix.Select(r => r.Code)));

        var metXbrlCode = allRows.Single(r => r.Code == "MET").XbrlCode;
        Assert.Equal("MET", metXbrlCode);

        RepoPaths.EnsureReferenceDatabaseExists();

        var generatedByCode = ReadXbrlCodesByDimensionCode(fixture.GeneratedConnection);
        using var referenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);
        var referenceByCode = ReadXbrlCodesByDimensionCode(referenceConnection);

        var commonCodes = generatedByCode.Keys.Intersect(referenceByCode.Keys, StringComparer.Ordinal).ToList();
        Assert.Equal(1089, commonCodes.Count);
        Assert.Contains("MET", commonCodes);

        var withoutMatch = commonCodes
            .Where(code => !generatedByCode[code].Overlaps(referenceByCode[code]))
            .ToList();

        Assert.True(
            withoutMatch.Count == 0,
            "Dimension codes without a DimensionXBRLCode matching the reference: " + string.Join(", ", withoutMatch));
    }

    private static Dictionary<string, HashSet<string>> ReadXbrlCodesByDimensionCode(SqliteConnection connection)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DimensionCode, DimensionXBRLCode FROM mDimension";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var code = reader.GetString(0);
            if (!result.TryGetValue(code, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                result[code] = set;
            }

            if (!reader.IsDBNull(1))
            {
                set.Add(reader.GetString(1));
            }
        }

        return result;
    }

    private static List<(string Code, string XbrlCode)> ReadCodeXbrlPairs(SqliteConnection connection)
    {
        var result = new List<(string, string)>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DimensionCode, DimensionXBRLCode FROM mDimension";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
        }

        return result;
    }

    /// <summary>
    /// The 11 duplicated codes -- each with TWO rows, DIFFERENT DOMAIN -- are exactly these,
    /// checked by name: it is the whole reason why <c>mDimension</c> does not collapse to one row
    /// per entity. If someone "deduplicates" this table, the 4.0 taxonomies will point to the wrong
    /// domain.
    /// </summary>
    [DataFact]
    public void MDimension_TheElevenDuplicatedCodes_HaveTwoRowsWithDifferentDomains()
    {
        var expected = new[] { "ECB", "ECC", "ECG", "ECW", "EXC", "qACA", "qAMZ", "qANN", "qAOV", "qJMM", "qTRH" };

        var duplicated = new List<string>();
        using (var command = fixture.GeneratedConnection.CreateCommand())
        {
            command.CommandText =
                "SELECT DimensionCode FROM mDimension GROUP BY DimensionCode HAVING COUNT(*) > 1 ORDER BY DimensionCode";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                duplicated.Add(reader.GetString(0));
            }
        }

        Assert.Equal(expected.OrderBy(c => c, StringComparer.Ordinal), duplicated);

        foreach (var code in expected)
        {
            var domainIds = ReadLongColumn(
                $"SELECT DISTINCT DomainID FROM mDimension WHERE DimensionCode = '{code}'");
            Assert.Equal(2, domainIds.Count);
        }
    }

    /// <summary>
    /// <c>qLEA</c>, <c>qLEB</c> and <c>qLES</c> are emitted. With the naive "last row wins" rule they
    /// would be lost -- this test is nominal, not a count: it would catch exactly that regression.
    /// </summary>
    [DataFact]
    public void MDimension_QLea_QLeb_QLes_AreEmitted()
    {
        foreach (var code in new[] { "qLEA", "qLEB", "qLES" })
        {
            Assert.Equal(1, ScalarLong($"SELECT COUNT(*) FROM mDimension WHERE DimensionCode = '{code}'"));
        }
    }

    /// <summary><c>IsTypedDimension</c> iff its domain is typed: 1,100 of 1,100 in ours (including <c>MET</c>).</summary>
    [DataFact]
    public void MDimension_IsTypedDimension_MatchesTheIsTypedDomainOfTheDomain_InAllRows()
    {
        var mismatches = ScalarLong(
            "SELECT COUNT(*) FROM mDimension dm JOIN mDomain d ON dm.DomainID = d.DomainID WHERE dm.IsTypedDimension <> d.IsTypedDomain");
        Assert.Equal(0, mismatches);
    }

    /// <summary>The split of <c>_NA</c> into <c>INT_NA</c>/<c>STR_NA</c>: 7 of 7, by name.</summary>
    [DataFact]
    public void MDimension_NASplit_ToIntNaOrStrNa_SevenOfSeven()
    {
        var expectedIntNa = new[] { "ii1713" };
        var expectedStrNa = new[] { "qABI", "qADP", "qADQ", "qADR", "qCDF", "qCFA" };

        foreach (var code in expectedIntNa)
        {
            Assert.Equal("INT_NA", DomainCodeOfDimension(code));
        }

        foreach (var code in expectedStrNa)
        {
            Assert.Equal("STR_NA", DomainCodeOfDimension(code));
        }
    }

    private string DomainCodeOfDimension(string dimensionCode)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText =
            "SELECT d.DomainCode FROM mDimension dm JOIN mDomain d ON dm.DomainID = d.DomainID WHERE dm.DimensionCode = $c";
        command.Parameters.AddWithValue("$c", dimensionCode);
        var value = command.ExecuteScalar();
        Assert.False(value is null or DBNull, $"Dimension '{dimensionCode}' was not found.");
        return (string)value!;
    }

    // ------------------------------------------------------------------
    // Expected gaps
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>MET</c> IS emitted (<c>DimensionID</c> 9999, see
    /// <see cref="MMetric_And_MDimension_AreNotDisjoint_FortyFiveCodesInBoth"/> and the class header
    /// comment), so it is not a gap. <c>TNS</c> remains the ONLY <c>mDimension</c> code that the
    /// reference has and we do not.
    /// </summary>
    [DataFact]
    public void MDimension_Tns_IsTheOnlyCodeStillMissing_MetIsAlreadyEmitted()
    {
        Assert.Equal(1, ScalarLong("SELECT COUNT(*) FROM mDimension WHERE DimensionCode = 'MET'"));
        Assert.Equal(0, ScalarLong("SELECT COUNT(*) FROM mDimension WHERE DimensionCode = 'TNS'"));

        RepoPaths.EnsureReferenceDatabaseExists();
        using var referenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);
        foreach (var code in new[] { "MET", "TNS" })
        {
            var refCount = ScalarLong(
                referenceConnection, $"SELECT COUNT(*) FROM mDimension WHERE DimensionCode = '{code}'");
            Assert.True(refCount > 0, $"The reference should have '{code}' -- if it no longer does, this gap has stopped being expected.");
        }
    }

    /// <summary><c>TEM</c>: false positive of the dimension rule (its <c>PropertyCategory</c> is <c>SE</c>). It must not be emitted.</summary>
    [DataFact]
    public void MDimension_Tem_IsNotEmitted()
    {
        Assert.Equal(0, ScalarLong("SELECT COUNT(*) FROM mDimension WHERE DimensionCode = 'TEM'"));
    }

    /// <summary>
    /// <c>mMetric.ReferencedHierarchyID</c>: 11 metrics where ONLY the reference has a hierarchy
    /// (we do not) -- named one by one.
    /// </summary>
    [DataFact]
    public void MMetric_ReferencedHierarchyID_ElevenCasesOnlyInTheReference()
    {
        RepoPaths.EnsureReferenceDatabaseExists();
        using var referenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);

        var codes = new[] { "ei205", "ei206", "ei209", "ei246", "ei367", "ei473", "ei50", "ei595", "ei604", "ei634", "ei635" };

        foreach (var code in codes)
        {
            var generatedHierarchy = HierarchyCodeOfMetric(fixture.GeneratedConnection, code);
            var referenceHierarchy = HierarchyCodeOfMetric(referenceConnection, code);

            Assert.Null(generatedHierarchy);
            Assert.NotNull(referenceHierarchy);
        }
    }

    /// <summary>
    /// A known reference divergence: the reference says <c>AP30_REL_2</c>, <c>GA9_REL_2</c> and
    /// <c>RP7_REL_2</c> (DPM 1.0 versioning suffix) where we say <c>AP30</c>, <c>GA9</c> and
    /// <c>RP7</c>, in the metrics <c>ei1395</c>, <c>ei1451</c> and <c>ei1492</c> respectively.
    /// Named cases, not failures.
    /// </summary>
    [DataFact]
    public void MMetric_ReferencedHierarchyID_ThreeCases_Rel2SuffixOfDpm1()
    {
        RepoPaths.EnsureReferenceDatabaseExists();
        using var referenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);

        var cases = new[]
        {
            ("ei1395", "AP30", "AP30_REL_2"),
            ("ei1451", "GA9", "GA9_REL_2"),
            ("ei1492", "RP7", "RP7_REL_2"),
        };

        foreach (var (metricCode, expectedGenerated, expectedReference) in cases)
        {
            Assert.Equal(expectedGenerated, HierarchyCodeOfMetric(fixture.GeneratedConnection, metricCode));
            Assert.Equal(expectedReference, HierarchyCodeOfMetric(referenceConnection, metricCode));
        }
    }

    private static string? HierarchyCodeOfMetric(SqliteConnection connection, string metricCode)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT h.HierarchyCode
            FROM mMetric mt
            JOIN mMember m ON mt.CorrespondingMemberID = m.MemberID
            LEFT JOIN mHierarchy h ON mt.ReferencedHierarchyID = h.HierarchyID
            WHERE m.MemberCode = $c
            """;
        command.Parameters.AddWithValue("$c", metricCode);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : (string)value;
    }

    // ------------------------------------------------------------------
    // Internal invariants, without looking at any reference
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>ReferencedDomainID</c> comes out through two independent paths and they must agree: the
    /// domain of the referenced hierarchy (what <c>mMetric</c> writes), and the
    /// <c>PropertyCategory</c> of the property (read directly from the source Access database).
    /// Measured: 426 of 427, with the single discrepancy named -- <c>PropertyID=7090</c> (code
    /// <c>ei706</c>), whose hierarchy is an <c>AT*</c> inherited from the <c>MET</c> domain while
    /// its <c>PropertyCategory</c> is <c>_NA</c>.
    /// </summary>
    [DataFact]
    public void MMetric_ReferencedDomainID_MatchesThatOfThePropertyCategory_Except7090()
    {
        RepoPaths.EnsureAccessDpm20DatabaseExists();

        using var reader = new Dpm20AccessReader(RepoPaths.AccessDpm20DatabasePath, cutoffReleaseCode: RepoPaths.Cutoff42ReleaseCode);
        reader.Open();

        var categories = reader.ReadCategories().ToList();
        var categoryCodeById = categories.ToDictionary(c => c.CategoryId, c => c.Code);
        var prCategoryId = categories.Single(c => c.Code == "_PR").CategoryId;

        var propertyIdByCode = reader.ReadItemCategories()
            .Where(ic => ic.CategoryId == prCategoryId)
            .ToDictionary(ic => ic.Code, ic => ic.ItemId, StringComparer.Ordinal);

        var domainCodesByPropertyId = reader.ReadPropertyCategories()
            .GroupBy(pc => pc.PropertyId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => categoryCodeById.GetValueOrDefault(x.CategoryId))
                      .Where(code => code is not null)
                      .Select(code => code!)
                      .ToHashSet(StringComparer.Ordinal));

        var rows = new List<(string MetricCode, string HierarchyDomainCode)>();
        using (var command = fixture.GeneratedConnection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT m.MemberCode, d.DomainCode
                FROM mMetric mt
                JOIN mMember m ON mt.CorrespondingMemberID = m.MemberID
                JOIN mDomain d ON mt.ReferencedDomainID = d.DomainID
                """;
            using var reader2 = command.ExecuteReader();
            while (reader2.Read())
            {
                rows.Add((reader2.GetString(0), reader2.GetString(1)));
            }
        }

        Assert.Equal(427, rows.Count);

        var mismatches = new List<string>();
        foreach (var (metricCode, hierarchyDomainCode) in rows)
        {
            if (!propertyIdByCode.TryGetValue(metricCode, out var propertyId))
            {
                mismatches.Add($"{metricCode}: no PropertyID in ItemCategory/_PR");
                continue;
            }

            if (!domainCodesByPropertyId.TryGetValue(propertyId, out var domainCodes) || domainCodes.Count == 0)
            {
                mismatches.Add($"{metricCode} (PropertyID={propertyId}): no PropertyCategory");
                continue;
            }

            if (!domainCodes.Contains(hierarchyDomainCode))
            {
                mismatches.Add(
                    $"{metricCode} (PropertyID={propertyId}): hierarchy says '{hierarchyDomainCode}', "
                    + $"PropertyCategory says {{{string.Join(",", domainCodes)}}}");
            }
        }

        // The NAMED exception -- PropertyID=7090 (ei706) -- and no other.
        Assert.Equal(
            ["ei706 (PropertyID=7090): hierarchy says 'MET', PropertyCategory says {_NA}"],
            mismatches);
    }

    /// <summary>
    /// <c>ReferencedDomainID</c> and <c>IsStartingMemberIncluded</c> go with
    /// <c>ReferencedHierarchyID</c>: either all three are present or none is, and
    /// <c>IsStartingMemberIncluded</c> = 1 exactly when there is a hierarchy.
    /// </summary>
    [DataFact]
    public void MMetric_DomainAndStartingInclusion_AlwaysGoWithTheHierarchy_AllThreeOrNone()
    {
        var domainWithoutHierarchyOrViceversa = ScalarLong(
            "SELECT COUNT(*) FROM mMetric WHERE (ReferencedDomainID IS NULL) <> (ReferencedHierarchyID IS NULL)");
        Assert.Equal(0, domainWithoutHierarchyOrViceversa);

        var startingMemberMismatch = ScalarLong(
            "SELECT COUNT(*) FROM mMetric WHERE (IsStartingMemberIncluded = 1) <> (ReferencedHierarchyID IS NOT NULL)");
        Assert.Equal(0, startingMemberMismatch);

        // And it is not an empty case: there are 427 rows with all three present.
        Assert.Equal(427, ScalarLong("SELECT COUNT(*) FROM mMetric WHERE ReferencedHierarchyID IS NOT NULL"));
    }

    /// <summary>
    /// Metric and dimension are NOT disjoint: there are codes in <c>mMetric</c> AND in
    /// <c>mDimension</c> at the same time. A test that assumed a partition (empty intersection)
    /// would be wrong -- this one checks exactly the opposite: that the intersection exists and
    /// contains, among others, the cases <c>FGT</c>, <c>CUA</c> and <c>GGA</c>.
    ///
    /// The measured count is 45: it includes <c>old-FGT</c> and <c>old-ei912</c>, which only start
    /// to exist in <c>mDimension</c> with the window crossing of property codes.
    /// </summary>
    [DataFact]
    public void MMetric_And_MDimension_AreNotDisjoint_FortyFiveCodesInBoth()
    {
        var metricCodes = ReadStringColumn(
            fixture.GeneratedConnection,
            "SELECT DISTINCT m.MemberCode FROM mMetric mt JOIN mMember m ON mt.CorrespondingMemberID = m.MemberID")
            .ToHashSet(StringComparer.Ordinal);

        var dimensionCodes = ReadStringColumn(fixture.GeneratedConnection, "SELECT DISTINCT DimensionCode FROM mDimension")
            .ToHashSet(StringComparer.Ordinal);

        var both = metricCodes.Intersect(dimensionCodes, StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(45, both.Count);
        Assert.Contains("FGT", both);
        Assert.Contains("CUA", both);
        Assert.Contains("GGA", both);
        Assert.Contains("old-FGT", both);
        Assert.Contains("old-ei912", both);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Dictionary<string, string> ReadDataTypeByMemberCode(SqliteConnection connection)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT m.MemberCode, mt.DataType FROM mMetric mt JOIN mMember m ON mt.CorrespondingMemberID = m.MemberID";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1))
            {
                result[reader.GetString(0)] = reader.GetString(1);
            }
        }

        return result;
    }

    private long CountRows(string table)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private long ScalarLong(string sql) => ScalarLong(fixture.GeneratedConnection, sql);

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private List<long> ReadLongColumn(string sql)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var values = new List<long>();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                values.Add(reader.GetInt64(0));
            }
        }

        return values;
    }

    private static List<string> ReadStringColumn(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var values = new List<string>();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                values.Add(reader.GetString(0));
            }
        }

        return values;
    }

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
