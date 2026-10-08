using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.All;

/// <summary>
/// Dictionary invariants A-DIC-02/03/04/09/10/11/13/15 and A-SEN-06, over <c>--all</c>. The
/// dictionary is not filtered by taxonomy, so these figures must match those measured over the
/// COMPLETE Access database, regardless of whether the selection is 1 taxonomy or all 128.
/// </summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class DictionaryInvariantTests
{
    private readonly AllFixture _fixture;

    public DictionaryInvariantTests(AllFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A-DIC-02: the converse of "typed domain => no default member" is NOT exact -- there are
    /// UNTYPED domains without a default member, and there are three, NAMED: <c>MET</c>, the
    /// <c>AS</c> "Accounting standard" and the <c>9999</c> sentinel. Informative layer, but with a
    /// pinned census: a fourth unnamed case is a sign that something else has lost its default
    /// member without anyone knowing why.
    /// </summary>
    [DataFact]
    public void UntypedDomainsWithoutDefaultMember_AreExactlyThreeNamedOnes()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT d."DomainCode" FROM "mDomain" d
            WHERE d."IsTypedDomain" = 0
              AND NOT EXISTS (SELECT 1 FROM "mMember" m WHERE m."DomainID" = d."DomainID" AND m."IsDefaultMember" = 1)
            """,
            1);

        var codes = rows.Select(r => r[0]).ToList();

        Assert.True(
            codes.Count == 3,
            $"{codes.Count} untyped domains without a default member (exactly 3 expected, A-DIC-02). " +
            $"Codes: {string.Join(",", codes)}");

        Assert.True(
            codes.Contains("MET") && codes.Contains("AS"),
            $"The untyped domains without a default member must include MET and AS (A-DIC-02). Found: {string.Join(",", codes)}");

        // The third one is the 9999 sentinel, identified by DomainID (its DomainCode has no
        // business value: empty string).
        var sentinelIsAmongThem = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mDomain" d
            WHERE d."DomainID" = 9999 AND d."IsTypedDomain" = 0
              AND NOT EXISTS (SELECT 1 FROM "mMember" m WHERE m."DomainID" = d."DomainID" AND m."IsDefaultMember" = 1)
            """);
        Assert.True(sentinelIsAmongThem == 1, "The 9999 sentinel domain is not among the three untyped domains without a default member (A-DIC-02).");
    }

    /// <summary>A-DIC-03: no domain with MORE than one default member.</summary>
    [DataFact]
    public void NoDomain_HasMoreThanOneDefaultMember()
    {
        var violatingDomains = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM (
                SELECT "DomainID" FROM "mMember" WHERE "IsDefaultMember" = 1
                GROUP BY "DomainID" HAVING COUNT(*) > 1
            )
            """);

        Assert.True(violatingDomains == 0, $"{violatingDomains} domains with more than one default member (A-DIC-03).");
    }

    /// <summary>
    /// A-DIC-04: mDimension.IsTypedDimension == mDomain.IsTypedDomain of its domain, in both directions.
    ///
    /// This test uses <c>&lt;&gt;</c> in SQL, which in SQLite (as in the standard) yields
    /// <c>NULL</c> -- not true -- when either side is <c>NULL</c>: a row with
    /// <c>IsTypedDimension IS NULL</c> does NOT fall into the <c>WHERE</c> and is therefore NOT
    /// counted as a mismatch. That is exactly the defect there was once: <c>IsTypedDimension</c>
    /// was <c>NULL</c> in all 864 rows and this test stayed green. The test is kept as it was
    /// (historical) and <see cref="DimensionIsTyped_IsNeverNull"/> is added next to it, with
    /// <c>IS NOT</c> (NULL-safe), so that a future regression cannot slip through the same gap.
    /// </summary>
    [DataFact]
    public void DimensionIsTyped_AlwaysMatchesItsDomainIsTyped()
    {
        var mismatches = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mDimension" dim
            JOIN "mDomain" dom ON dom."DomainID" = dim."DomainID"
            WHERE dim."IsTypedDimension" <> dom."IsTypedDomain"
            """);

        Assert.True(mismatches == 0, $"{mismatches} dimensions whose IsTypedDimension differs from the IsTypedDomain of their domain (A-DIC-04).");
    }

    /// <summary>
    /// Independent and NULL-safe measurement (see the note on the sibling test
    /// <see cref="DimensionIsTyped_AlwaysMatchesItsDomainIsTyped"/>, whose <c>&lt;&gt;</c> did not
    /// detect NULL). Two checks: (1) <c>IsTypedDimension</c> is not <c>NULL</c> in ANY of the 864
    /// rows of <c>mDimension</c> over <c>--all</c>; (2) using <c>IS NOT</c> (SQLite's NULL-safe
    /// comparison) it matches the <c>IsTypedDomain</c> of its domain in 864/864.
    /// </summary>
    [DataFact]
    public void DimensionIsTyped_IsNeverNull()
    {
        var totalDimensions = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mDimension\"");
        Assert.True(totalDimensions == 864, $"mDimension has {totalDimensions} rows (exactly 864 expected, measured over --all).");

        var nullCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mDimension\" WHERE \"IsTypedDimension\" IS NULL");
        Assert.True(nullCount == 0, $"{nullCount} of {totalDimensions} mDimension rows with a NULL IsTypedDimension (0 expected).");

        var mismatchesNullSafe = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mDimension" dim
            JOIN "mDomain" dom ON dom."DomainID" = dim."DomainID"
            WHERE dim."IsTypedDimension" IS NOT dom."IsTypedDomain"
            """);
        Assert.True(
            mismatchesNullSafe == 0,
            $"{mismatchesNullSafe} of {totalDimensions} dimensions whose IsTypedDimension (NULL-safe comparison, " +
            "IS NOT) differs from the IsTypedDomain of their domain (864/864 matching expected).");
    }

    /// <summary>
    /// Measured row by row over <c>--all</c> (128 taxonomies), without sampling, on OUR output:
    /// <c>mDomain.DomainXBRLCode</c> is <c>eba_exp:</c> + <c>DomainCode</c> if the domain is NOT
    /// typed, or <c>eba_typ:</c> + <c>DomainCode</c> if it is -- with EXACTLY two named
    /// exceptions: the <c>MET</c> domain (fixed business code <c>"MET"</c> with no prefix) and the
    /// <c>DomainID=9999</c> sentinel (empty string). Any other unnamed exception is a regression.
    /// </summary>
    [DataFact]
    public void DomainXbrlCode_FollowsThePrefixRule_WithExactlyTwoNamedExceptions()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT \"DomainID\", \"DomainCode\", \"IsTypedDomain\", \"DomainXBRLCode\" FROM \"mDomain\"",
            4);

        Assert.True(rows.Count > 0, "mDomain is empty; nothing to check (unexpected over --all).");

        var unexpectedExceptions = new List<string>();
        var namedExceptionCount = 0;

        foreach (var row in rows)
        {
            var domainId = row[0]!;
            var domainCode = row[1]!;
            var isTypedDomain = row[2] is "1" or "True" or "true";
            var domainXbrlCode = row[3];

            if (domainCode == "MET")
            {
                namedExceptionCount++;
                if (domainXbrlCode != "MET")
                {
                    unexpectedExceptions.Add($"DomainID={domainId} (MET): DomainXBRLCode='{domainXbrlCode}', 'MET' expected");
                }

                continue;
            }

            if (domainId == "9999")
            {
                namedExceptionCount++;
                if (domainXbrlCode != string.Empty)
                {
                    unexpectedExceptions.Add($"DomainID=9999 (sentinel): DomainXBRLCode='{domainXbrlCode}', empty string expected");
                }

                continue;
            }

            var expected = (isTypedDomain ? "eba_typ:" : "eba_exp:") + domainCode;
            if (domainXbrlCode != expected)
            {
                unexpectedExceptions.Add($"DomainID={domainId} DomainCode='{domainCode}': DomainXBRLCode='{domainXbrlCode}', '{expected}' expected");
            }
        }

        Assert.True(namedExceptionCount == 2, $"{namedExceptionCount} domains matched the named exceptions (MET, 9999); exactly 2 expected.");

        Assert.True(
            unexpectedExceptions.Count == 0,
            $"{unexpectedExceptions.Count} of {rows.Count} domains do not follow the eba_exp:/eba_typ: + DomainCode rule " +
            $"outside the two named exceptions (MET, 9999 sentinel). Examples: " +
            string.Join("; ", unexpectedExceptions.Take(20)));
    }

    /// <summary>A-DIC-09: |mMetric| == number of members of the MET domain.</summary>
    [DataFact]
    public void MetricRowCount_EqualsMetDomainMemberCount()
    {
        var metricCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mMetric\"");
        var metMemberCount = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mMember" m
            JOIN "mDomain" d ON d."DomainID" = m."DomainID"
            WHERE d."DomainCode" = 'MET'
            """);

        Assert.True(
            metricCount == metMemberCount,
            $"mMetric has {metricCount} rows versus {metMemberCount} members of the MET domain (A-DIC-09).");
    }

    /// <summary>A-DIC-10: the only dimension XBRL code WITHOUT ':' is <c>MET</c>.</summary>
    [DataFact]
    public void OnlyDimensionXbrlCode_WithoutColon_IsMet()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT \"DimensionXBRLCode\" FROM \"mDimension\" WHERE \"DimensionXBRLCode\" NOT LIKE '%:%'",
            1);

        var codes = rows.Select(r => r[0]).ToList();

        Assert.True(
            codes.Count == 1 && codes[0] == "MET",
            $"Dimension XBRL codes without ':': [{string.Join(",", codes)}] (exactly 1, 'MET', expected, A-DIC-10).");
    }

    /// <summary>
    /// A-DIC-11: the only dimension XBRL codes WITH a release suffix are the 5 that the Access
    /// already carries that way (verbatim): <c>eba_dim_4.0:</c> EXC, ECG, ECB, ECC, ECW.
    /// </summary>
    [DataFact]
    public void OnlyFiveDimensionXbrlCodes_CarryAReleaseSuffix_AsTheAccessAlreadyDoes()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT \"DimensionXBRLCode\" FROM \"mDimension\" WHERE \"DimensionXBRLCode\" LIKE 'eba_dim_%:%'",
            1);

        var codes = rows.Select(r => r[0]!).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var expected = new[] { "eba_dim_4.0:ECB", "eba_dim_4.0:ECC", "eba_dim_4.0:ECG", "eba_dim_4.0:ECW", "eba_dim_4.0:EXC" };

        Assert.True(
            codes.SequenceEqual(expected),
            $"Dimension XBRL codes with a release suffix: [{string.Join(",", codes)}] " +
            $"(exactly [{string.Join(",", expected)}] expected, A-DIC-11).");
    }

    /// <summary>A-DIC-13: mConceptTranslation has no orphans; Role only label/description; LanguageID=1 always.</summary>
    [DataFact]
    public void ConceptTranslation_HasNoOrphans_ValidRoles_AndLanguageIdIsAlwaysOne()
    {
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mConceptTranslation", "ConceptID", "mConcept", "ConceptID");
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mConceptTranslation", "LanguageID", "mLanguage", "LanguageID");

        var invalidRoles = QueryHelpers.Scalar(
            _fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mConceptTranslation\" WHERE \"Role\" NOT IN ('label', 'description')");
        Assert.True(invalidRoles == 0, $"{invalidRoles} mConceptTranslation rows with a Role outside {{label, description}} (A-DIC-13).");

        var wrongLanguage = QueryHelpers.Scalar(
            _fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mConceptTranslation\" WHERE \"LanguageID\" <> 1");
        Assert.True(wrongLanguage == 0, $"{wrongLanguage} mConceptTranslation rows with a LanguageID other than 1 (A-DIC-13).");
    }

    /// <summary>A-DIC-15: mOwner has exactly 3 rows with the fixed values; mOwnerParent exactly 1 (1 -> 3); mLanguage exactly 1 ('en').</summary>
    [DataFact]
    public void Owner_OwnerParent_Language_HaveTheFixedRowCountsAndValues()
    {
        var ownerCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mOwner\"");
        Assert.True(ownerCount == 3, $"mOwner has {ownerCount} rows (exactly 3 expected, A-DIC-15).");

        var technicalOwner = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT \"OwnerName\", \"OwnerCode\", \"OwnerPrefix\" FROM \"mOwner\" WHERE \"OwnerID\" = 2", 3);
        Assert.True(technicalOwner.Count == 1, "mOwner.OwnerID=2 ('Technical') is missing.");
        Assert.Equal("Technical", technicalOwner[0][0]);
        Assert.Equal("Technical", technicalOwner[0][1]);
        Assert.Equal("Technical", technicalOwner[0][2]);

        var eurofilingOwner = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT \"OwnerName\", \"OwnerCode\", \"OwnerPrefix\" FROM \"mOwner\" WHERE \"OwnerID\" = 3", 3);
        Assert.True(eurofilingOwner.Count == 1, "mOwner.OwnerID=3 ('Eurofiling') is missing.");
        Assert.Equal("Eurofiling", eurofilingOwner[0][0]);
        Assert.Equal("eu", eurofilingOwner[0][1]);
        Assert.Equal("eu", eurofilingOwner[0][2]);

        var ownerParentRows = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"OwnerID\", \"ParentOwnerID\" FROM \"mOwnerParent\"", 2);
        Assert.True(ownerParentRows.Count == 1, $"mOwnerParent has {ownerParentRows.Count} rows (exactly 1 expected, A-DIC-15).");
        Assert.Equal("1", ownerParentRows[0][0]);
        Assert.Equal("3", ownerParentRows[0][1]);

        var languageRows = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"IsoCode\" FROM \"mLanguage\"", 1);
        Assert.True(languageRows.Count == 1, $"mLanguage has {languageRows.Count} rows (exactly 1 expected, A-DIC-15).");
        Assert.Equal("en", languageRows[0][0]);
    }

    /// <summary>A-SEN-06: no mMember with MemberLabel='&lt;Key value&gt;' nor with a NULL DomainID.</summary>
    [DataFact]
    public void NoMember_HasThePlaceholderLabel_OrANullDomainId()
    {
        var placeholderLabel = QueryHelpers.Scalar(
            _fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mMember\" WHERE \"MemberLabel\" = '<Key value>'");
        Assert.True(placeholderLabel == 0, $"{placeholderLabel} members with MemberLabel='<Key value>' (A-SEN-06).");

        var nullDomain = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mMember\" WHERE \"DomainID\" IS NULL");
        Assert.True(nullDomain == 0, $"{nullDomain} members with a NULL DomainID (A-SEN-06).");
    }
}
