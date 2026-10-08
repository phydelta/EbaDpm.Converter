namespace EbaDpm.Converter.Tests.Dictionary;

/// <summary>
/// The metrics dimension is NOT synthesised -- the Access database does model it, as <c>ATY</c>
/// (<c>DimensionID</c> 100, label "Metric", domain <c>AT</c>, 31,682 categorisations) -- and it is
/// RENAMED when emitted, exactly as its domain is renamed: <c>DimensionCode</c> = <c>MET</c>,
/// <c>DimensionLabel</c> = "Metric dimension", <c>DimensionXBRLCode</c> = <c>MET</c>, keeping the
/// source <c>DimensionID</c> and <c>ConceptID</c>, and its <c>DimensionDescription</c> verbatim.
///
/// An earlier premise ("the Access does not model it as a Dimension: COUNT(*) = 0") was false:
/// it searched by the DESTINATION code (<c>MET</c>) instead of by the role of the dimension.
/// With synthesis, TWO rows were emitted where the model has ONE: the synthetic <c>MET</c>, with
/// not a single categorisation using it, and the real <c>ATY</c>, with all 31,682 -- an
/// incoherence the output must not have. Critical layer.
/// </summary>
[Collection("Dictionary")]
public sealed class MetDimensionTests
{
    private readonly DictionaryFixture _fixture;

    public MetDimensionTests(DictionaryFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// The metrics domain identified by its role: the only one whose <c>DomainCode</c> is
    /// <c>MET</c> after the renaming. It is resolved by business key, never by ID.
    /// </summary>
    private int GetMetricsDomainId()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection, "SELECT \"DomainID\" FROM \"mDomain\" WHERE \"DomainCode\" = 'MET'", 1);

        Assert.True(rows.Count == 1, $"Exactly 1 domain with DomainCode='MET' was expected; there are {rows.Count}.");
        return int.Parse(rows[0][0]!);
    }

    /// <summary>
    /// Corroboration measurable WITHOUT looking at any reference: of the 864 dimensions of the
    /// Access, exactly ONE has a NULL <c>DimensionXbrlCode</c>, and it is the metrics one. If the
    /// source changed and this stopped holding, <c>IdentifyMetricsDimensionId</c> in
    /// <c>DictionaryLoader</c> itself already stops the conversion (explicit exception); this test
    /// makes it visible from the test side as well, with a fixed control figure.
    /// </summary>
    [DataFact]
    public void Access_HasExactlyOneDimensionWithNullXbrlCode_AndItIsTheMetricsDimension()
    {
        var accessDimensions = _fixture.AccessReader.ReadDimensions().ToList();

        var nullXbrlCodeDimensions = accessDimensions.Where(d => d.DimensionXbrlCode is null).ToList();

        Assert.True(
            nullXbrlCodeDimensions.Count == 1,
            $"Exactly 1 of the {accessDimensions.Count} Access dimensions with a NULL " +
            $"DimensionXbrlCode was expected; there are {nullXbrlCodeDimensions.Count}: " +
            $"[{string.Join(",", nullXbrlCodeDimensions.Select(d => d.DimensionId))}].");

        var accessMetricsDimension = nullXbrlCodeDimensions[0];
        Assert.Equal("ATY", accessMetricsDimension.DimensionCode);
        Assert.Equal("Metric", accessMetricsDimension.DimensionLabel);

        // And it is the same row that the destination renames to MET (by DimensionID, a stable
        // key within THIS SAME database; the MemberID renumbering does not touch it).
        var generatedRow = QueryHelpers.Rows(
                _fixture.GeneratedConnection,
                $"SELECT \"DimensionCode\" FROM \"mDimension\" WHERE \"DimensionID\" = {accessMetricsDimension.DimensionId}",
                1)
            .Single();
        Assert.Equal("MET", generatedRow[0]);
    }

    [DataFact]
    public void MetDimension_ExistsExactlyOnce_WithExactBusinessKey()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT \"DimensionID\" FROM \"mDimension\" WHERE \"DimensionCode\" = 'MET' AND \"DimensionLabel\" = 'Metric dimension' AND \"DimensionXBRLCode\" = 'MET'",
            1);

        Assert.True(
            rows.Count == 1,
            $"Exactly 1 dimension with DimensionCode='MET', DimensionLabel='Metric dimension' " +
            $"and DimensionXBRLCode='MET' was expected; there are {rows.Count}.");
    }

    [DataFact]
    public void NoEmittedDimension_HasAccessCodeATY_OrNullXbrlCode()
    {
        // The renaming must be TOTAL: neither the raw Access code (ATY) nor the NULL XBRLCode it
        // had before being renamed may survive in what is emitted. If either appeared, the
        // renaming would have been applied halfway (e.g. renaming the code but not the XBRLCode,
        // or vice versa).
        var withAccessCode = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mDimension\" WHERE \"DimensionCode\" = 'ATY'");
        Assert.Equal(0, withAccessCode);

        var withNullXbrlCode = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mDimension\" WHERE \"DimensionXBRLCode\" IS NULL");
        Assert.Equal(0, withNullXbrlCode);
    }

    [DataFact]
    public void MetDimension_KeepsAccessDimensionIdAndConceptId_AndVerbatimDescription()
    {
        var accessMetricsDimension = _fixture.AccessReader.ReadDimensions().Single(d => d.DimensionXbrlCode is null);

        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT \"DimensionID\", \"ConceptID\", \"DimensionDescription\", \"DomainID\" FROM \"mDimension\" WHERE \"DimensionCode\" = 'MET'",
            4);

        Assert.True(rows.Count == 1, $"Exactly 1 mDimension row with DimensionCode='MET' was expected; there are {rows.Count}.");
        var row = rows[0];

        Assert.Equal(accessMetricsDimension.DimensionId.ToString(), row[0]); // DimensionID from the Access, NOT synthetic
        Assert.Equal(accessMetricsDimension.ConceptId?.ToString(), row[1]); // ConceptID from the Access, NOT synthetic
        Assert.Equal(accessMetricsDimension.DimensionDescription, row[2]); // DimensionDescription verbatim from the Access

        var metricsDomainId = GetMetricsDomainId();
        Assert.Equal(metricsDomainId.ToString(), row[3]); // DomainID: the metrics domain
        Assert.Equal(accessMetricsDimension.DomainId, metricsDomainId); // and it matches the Access DomainID (the domain renaming applies to the SAME row)
    }

    [DataFact]
    public void MetDimension_Concept_IsTheAccessConcept_WithDimensionTypeAndNullReleaseId()
    {
        var conceptId = int.Parse(
            QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"ConceptID\" FROM \"mDimension\" WHERE \"DimensionCode\" = 'MET'", 1)
                .Single()[0]!);

        // There is no synthetic ConceptID for this dimension any more -- it must exist in the Access.
        var accessConcept = _fixture.AccessReader.ReadConceptsByIds([conceptId]).ToList();
        Assert.True(accessConcept.Count == 1, $"The ConceptID {conceptId} of the MET dimension should exist in Access.Concept (it is no longer synthetic); there are {accessConcept.Count}.");

        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            $"SELECT \"ConceptType\", \"ReleaseID\" FROM \"mConcept\" WHERE \"ConceptID\" = {conceptId}",
            2);

        Assert.True(rows.Count == 1, $"mConcept has no row for the ConceptID {conceptId} of the MET dimension; orphan.");
        Assert.Equal("Dimension", rows[0][0]);
        Assert.Null(rows[0][1]); // ReleaseID NULL (MET carries no release suffix, its Access DimensionXbrlCode was NULL)
    }

    [DataFact]
    public void MetDimension_ConceptTranslation_SeedsLabelFromRenamedRow()
    {
        // The translation is synthesised from the EMITTED *Label, so it must carry
        // "Metric dimension" (the renamed label), not "Metric" (the original Access label).
        var conceptId = int.Parse(
            QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"ConceptID\" FROM \"mDimension\" WHERE \"DimensionCode\" = 'MET'", 1)
                .Single()[0]!);

        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            $"""
            SELECT "LanguageID", "Text", "Role" FROM "mConceptTranslation"
            WHERE "ConceptID" = {conceptId} AND "Role" = 'label'
            """,
            3);

        Assert.True(rows.Count == 1, $"Exactly 1 'label' translation for the ConceptID {conceptId} of the MET dimension was expected; there are {rows.Count}.");
        Assert.Equal("1", rows[0][0]); // LanguageID = 1 (mLanguage, single row, 'en')
        Assert.Equal("Metric dimension", rows[0][1]);
    }

    [DataFact]
    public void MetDimension_IsTheTargetOfAllMetricCategorisations_AndNoUnusedFormerSyntheticRowRemains()
    {
        // There is no longer an unused "MET" row next to an "ATY" row holding the 31,682
        // categorisations -- it is the SAME row. This is verified on the Access itself (without
        // looking at any reference): the metrics dimension of the Access is the same one that is
        // now emitted as "MET".
        var accessMetricsDimensionId = _fixture.AccessReader.ReadDimensions().Single(d => d.DimensionXbrlCode is null).DimensionId;

        var metDimensionId = int.Parse(
            QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"DimensionID\" FROM \"mDimension\" WHERE \"DimensionCode\" = 'MET'", 1)
                .Single()[0]!);

        Assert.Equal(accessMetricsDimensionId, metDimensionId);

        // And NO other emitted dimension remains that carries DomainID = the metrics domain
        // (which would be the trace of a parallel synthetic row surviving by mistake).
        var strayCandidates = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            $"""
            SELECT COUNT(*) FROM "mDimension" d
            WHERE d."DomainID" = {GetMetricsDomainId()} AND d."DimensionID" <> {metDimensionId}
            """);
        Assert.Equal(0, strayCandidates);
    }

    [DataFact]
    public void MOrdinateCategorisation_HasNoOrphansTowardsMDimension() =>
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mOrdinateCategorisation", "DimensionID", "mDimension", "DimensionID");

    [DataFact]
    public void MetDimension_IsUsedByCellCategorisationsInReference42()
    {
        // Independent evidence (does not depend on our output): in the 4.2 reference, the MET
        // dimension is used by 6,635 cell categorisations -- it is not a decorative row.
        var usageCount = QueryHelpers.Scalar(
            _fixture.Reference42Connection,
            """
            SELECT COUNT(*) FROM "mOrdinateCategorisation" oc
            JOIN "mDimension" d ON d."DimensionID" = oc."DimensionID"
            WHERE d."DimensionCode" = 'MET'
            """);

        Assert.True(usageCount > 0, "The MET dimension is not used by any categorisation in EBA_4.2_Hotfix.db (the evidence is not reproducible).");
    }
}
