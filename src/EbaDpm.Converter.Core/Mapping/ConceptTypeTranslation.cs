namespace EbaDpm.Converter.Core.Mapping;

/// <summary>
/// Translation of <c>Access.Concept.ConceptType</c> (SOURCE vocabulary) to
/// <c>mConcept.ConceptType</c> (TARGET vocabulary).
///
/// This is the third occurrence of the same pattern: writing to the target a literal taken as-is
/// from the source without translating it. The other two are the open-axis sentinel (<c>999</c>
/// in Access, <c>9999</c> in the target) and the metrics dimension (<c>ATY</c> in Access,
/// <c>MET</c> in the target). Before writing a <c>ConceptType</c> read from <c>Access.Concept</c>
/// into <c>mConcept</c>, ALWAYS go through <see cref="ToDestinationVocabulary"/>.
///
/// <b>The TARGET vocabulary</b> (per the EIOPA specification): <c>Axis</c>, <c>Ordinate</c>,
/// <c>Dimension</c>, <c>Domain</c>, <c>Hierarchy</c>, <c>HierarchyNode</c>, <c>Member</c>,
/// <c>Module</c>, <c>Release</c>, <c>ReportingFramework</c>, <c>Table</c>,
/// <c>TemplateOrTable</c>, <c>Taxonomy</c>.
///
/// Ten of those categories match literally the name of their entity in Access and are written
/// VERBATIM by their own loader, without going through this class: <c>Ordinate</c>,
/// <c>HierarchyNode</c>, <c>Member</c>, <c>Axis</c>, <c>Hierarchy</c>, <c>Dimension</c>,
/// <c>Domain</c>, <c>Taxonomy</c>, <c>Module</c>, <c>ReportingFramework</c>. The other two
/// (<c>Table</c>, <c>TemplateOrTable</c>) are ONLY reached by translating three source
/// <c>ConceptType</c> values (measured on a full <c>--all</c> run): <c>TableVersion</c> (2,561)
/// → <c>Table</c>; <c>TableGroup</c> (692) + <c>Template</c> (655) → <c>TemplateOrTable</c>. The
/// source <c>ConceptType='Table'</c> itself (950 rows, the <c>Access.Table</c> entity) does not
/// appear here because its concepts are removed entirely: that entity produces no target row
/// (<see cref="TemplateOrTableLoader"/>), so its concept would not describe anything.
/// </summary>
internal static class ConceptTypeTranslation
{
    private static readonly IReadOnlyDictionary<string, string> OriginToDestination =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TableVersion"] = "Table",
            ["TableGroup"] = "TemplateOrTable",
            ["Template"] = "TemplateOrTable",
        };

    /// <summary>
    /// Translates a <c>ConceptType</c> read from <c>Access.Concept</c> to the TARGET vocabulary. If
    /// it is not in the translation table it is returned VERBATIM: this is the case for the ten
    /// categories that already match between source and target (and for any unexpected value,
    /// which is not silently repaired here; schema validation will flag it if needed).
    /// </summary>
    public static string? ToDestinationVocabulary(string? accessConceptType)
        => accessConceptType is not null && OriginToDestination.TryGetValue(accessConceptType, out var destination)
            ? destination
            : accessConceptType;
}
