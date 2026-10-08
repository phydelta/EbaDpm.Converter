using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Mapping;

/// <summary>
/// Shared writer for <c>mConceptTranslation</c> from seeds (ConceptID, Label, Description).
/// Translations are SYNTHESISED from the <c>*Label</c> columns that each loader already emits in
/// its own table; they are never invented.
///
/// Originally only <see cref="DictionaryLoader"/> seeded <c>mConceptTranslation</c> (domains,
/// members, dimensions, hierarchies), which left 89 % of the concepts untranslated: axes,
/// ordinates, templates/tables, modules, taxonomies and frameworks never went through here. The
/// pattern now covers all of them; every loader that creates new <c>ConceptID</c>s calls
/// <see cref="Write"/> with ITS OWN seeds. There is no need to coordinate seeds between loaders
/// because each one works on a set of <c>ConceptID</c>s disjoint from the rest (one per source
/// entity).
/// </summary>
internal static class ConceptTranslationWriter
{
    /// <summary>
    /// Writes one <c>role='label'</c> row for each seed with a non-empty <c>Label</c> and one
    /// <c>role='description'</c> row for each seed with a non-empty <c>Description</c>.
    ///
    /// The PK of <c>mConceptTranslation</c> is (<c>ConceptID</c>, <c>LanguageID</c>,
    /// <c>Role</c>): if the same <c>ConceptID</c> appears more than once in <paramref name="seeds"/>
    /// (e.g. a <c>TableVID</c> shared by several taxonomies also shares its <c>TableVersion</c>
    /// <c>ConceptID</c>), the FIRST translation seen for each (<c>ConceptID</c>, <c>Role</c>) is
    /// kept and the following ones are silently discarded. This is not a relaxation: it is the same
    /// business object, so its label is the same.
    /// </summary>
    public static int Write(
        SqliteConnection destination,
        IEnumerable<(int ConceptId, string? Label, string? Description)> seeds)
    {
        using var writer = new SqliteBatchWriter(destination, "mConceptTranslation", ["ConceptID", "LanguageID", "Text", "Role"]);
        var written = new HashSet<(int ConceptId, string Role)>();

        foreach (var seed in seeds)
        {
            if (!string.IsNullOrWhiteSpace(seed.Label) && written.Add((seed.ConceptId, "label")))
            {
                writer.AddRow(seed.ConceptId, 1, seed.Label, "label");
            }

            if (!string.IsNullOrWhiteSpace(seed.Description) && written.Add((seed.ConceptId, "description")))
            {
                writer.AddRow(seed.ConceptId, 1, seed.Description, "description");
            }
        }

        return (int)writer.RowsWritten;
    }
}
