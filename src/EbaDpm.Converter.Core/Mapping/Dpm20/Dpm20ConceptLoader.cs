using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Mapping.Dpm20;

/// <summary>
/// Mints <c>mConcept</c> and <c>mConceptTranslation</c> entirely, in a FINAL PHASE of its own —
/// it runs AFTER the six DPM 2.0 loaders (<see cref="Dpm20SkeletonLoader"/>,
/// <see cref="Dpm20DictionaryLoader"/>, <see cref="Dpm20HierarchyNodeLoader"/>,
/// <see cref="Dpm20StructureLoader"/>, <see cref="Dpm20AxisAndCellLoader"/>,
/// <see cref="Dpm20ModuleLoader"/>) have already written their thirteen tables with
/// <c>ConceptID</c> set to NULL.
///
/// <b>DPM 2.0 has no <c>Concept</c> table in the source</b>: where DPM 1.0 copies the
/// <c>ConceptID</c> from Access, here there is nothing to copy — the concept is an entity of the
/// DESTINATION, minted here.
///
/// <b>It is a final phase that READS what has already been written, not a change in the six
/// loaders</b>: a counter spread across the loaders would have coupled their execution order and
/// made the <c>ConceptID</c> of one entity depend on whether another table was loaded before —
/// the non-determinism that must be avoided. Here the thirteen tables are traversed in a FIXED
/// ORDER (the same execution order of the loaders in <c>CliRunner.RunConvertDpm20</c>: skeleton,
/// dictionary, hierarchy tree, structure, axes/cells, modules) and, within each table, by
/// ascending primary key — deterministic because that key was already assigned deterministically
/// upstream.
///
/// <b>The thirteen types</b> (<c>ValidationRule</c> is out of scope): <c>Release</c>,
/// <c>ReportingFramework</c>, <c>Taxonomy</c>, <c>Domain</c>, <c>Member</c>, <c>Dimension</c>,
/// <c>Hierarchy</c>, <c>HierarchyNode</c>, <c>Axis</c>, <c>Ordinate</c>, <c>TemplateOrTable</c>,
/// <c>Table</c>, <c>Module</c>.
///
/// <b><c>mConceptTranslation</c></b>: one row with role <c>label</c> per concept, with the text of
/// the label ALREADY EMITTED by the corresponding loader — never invented. <b>7 concepts have no
/// label</b>: the 5 <c>Release</c> rows (<c>mRelease</c> has no label column — there is no row to
/// emit, not a NULL), the <c>MET</c> dimension (<c>DimensionID=9999</c>) and the sentinel member
/// <c>Open</c> — these last two DO have a label in their table, but are explicitly excluded. All
/// the rest, including <c>Taxonomy</c> (whose <c>TaxonomyLabel</c> is always NULL), carry their
/// <c>mConceptTranslation</c> row with whatever text their label column has — NULL included: it is
/// what the entity already has emitted, no more and no less. No <c>description</c> role is
/// emitted — the 12 source texts of the reference do not exist in any DPM 2.0 table.
///
/// <b><c>mConcept.ReleaseID</c></b>: comes from the suffix of the XBRL code/<c>TaxonomyCode</c>
/// that WE emit for that same entity, and is NULL when there is no suffix. Only <c>Dimension</c>
/// (<c>DimensionXBRLCode</c>, <c>eba_dim_{release}:code</c>), <c>Member</c>
/// (<c>MemberXBRLCode</c>, same suffix form — in practice always NULL because the dictionary
/// loader never composes it with a suffix) and <c>Taxonomy</c> (<c>TaxonomyCode</c>,
/// <c>framework release</c>) can carry it; the other ten types ALWAYS go to NULL, including
/// <c>Hierarchy</c> (the field has no referent in our output: a single row is emitted per
/// hierarchy) and <c>Release</c> (a release does not point to itself).
///
/// <c>OwnerID</c> = 1 (EBA, the only owner). <c>CreationDate</c>, <c>ModificationDate</c>,
/// <c>FromDate</c> and <c>ToDate</c> are always NULL (the reference has only 50 non-null out of
/// 79,509; there is no pattern to derive).
/// </summary>
public static class Dpm20ConceptLoader
{
    /// <summary>Count of rows written, for the CLI report.</summary>
    public sealed record Result(
        int ConceptRows,
        int ConceptTranslationRows,
        IReadOnlyDictionary<string, int> ConceptRowsByType,
        int ConceptsWithReleaseId);

    private const int OwnerId = 1; // EBA (mOwner), the only owner.
    private const int LanguageId = 1; // the only emitted language (mLanguage).

    // DimensionID of the MET dimension (see Dpm20DictionaryLoader.WriteMetDimension), measured as a
    // literal in two references — not a minted counter. The same constant as
    // Dpm20AxisAndCellLoader.MetDimensionId, defined here too because it is private there.
    private const int MetDimensionId = 9999;

    /// <summary>
    /// An emitted entity, ready to mint a concept: its source table/column (for the later
    /// <c>ConceptID</c> update), the concept type, its already emitted label (if the table has a
    /// label column and this entity is not excluded), and its already resolved <c>ReleaseID</c>.
    /// </summary>
    private sealed record EntitySeed(
        string TableName,
        string IdColumnName,
        int EntityId,
        string ConceptType,
        bool EmitTranslation,
        string? Label,
        int? ReleaseId);

    public static Result Load(SqliteConnection destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var releaseIdByCode = ReadReleaseIdByCode(destination);
        var sentinelMemberDomainId = FindSentinelDomainId(destination);

        // Fixed order: the same execution order of the six loaders in
        // CliRunner.RunConvertDpm20. Within each table, by ascending primary key.
        var seeds = new List<EntitySeed>();
        seeds.AddRange(ReadReleaseSeeds(destination));
        seeds.AddRange(ReadReportingFrameworkSeeds(destination));
        seeds.AddRange(ReadTaxonomySeeds(destination, releaseIdByCode));
        seeds.AddRange(ReadDomainSeeds(destination, sentinelMemberDomainId));
        seeds.AddRange(ReadMemberSeeds(destination, sentinelMemberDomainId, releaseIdByCode));
        seeds.AddRange(ReadDimensionSeeds(destination, releaseIdByCode));
        seeds.AddRange(ReadHierarchySeeds(destination));
        seeds.AddRange(ReadHierarchyNodeSeeds(destination));
        seeds.AddRange(ReadAxisSeeds(destination));
        seeds.AddRange(ReadOrdinateSeeds(destination));
        seeds.AddRange(ReadTemplateOrTableSeeds(destination));
        seeds.AddRange(ReadTableSeeds(destination));
        seeds.AddRange(ReadModuleSeeds(destination));

        // Minting: ConceptID = (1-based) position in the order fixed above.
        var conceptIds = new int[seeds.Count];
        for (var i = 0; i < seeds.Count; i++)
        {
            conceptIds[i] = i + 1;
        }

        var rowsByType = new Dictionary<string, int>(StringComparer.Ordinal);
        var withReleaseId = 0;

        using (var writer = new SqliteBatchWriter(
            destination,
            "mConcept",
            ["ConceptID", "ConceptType", "OwnerID", "ReleaseID", "CreationDate", "ModificationDate", "FromDate", "ToDate"]))
        {
            for (var i = 0; i < seeds.Count; i++)
            {
                var seed = seeds[i];
                writer.AddRow(conceptIds[i], seed.ConceptType, OwnerId, seed.ReleaseId, null, null, null, null);
                rowsByType[seed.ConceptType] = rowsByType.GetValueOrDefault(seed.ConceptType) + 1;
                if (seed.ReleaseId is not null)
                {
                    withReleaseId++;
                }
            }
        }

        var translationRows = 0;
        using (var writer = new SqliteBatchWriter(
            destination, "mConceptTranslation", ["ConceptID", "LanguageID", "Text", "Role"]))
        {
            for (var i = 0; i < seeds.Count; i++)
            {
                var seed = seeds[i];
                if (!seed.EmitTranslation)
                {
                    continue; // Release (no label column), MET, sentinel.
                }

                writer.AddRow(conceptIds[i], LanguageId, seed.Label, "label");
                translationRows++;
            }
        }

        // The ConceptID update in the thirteen tables goes AFTER closing the two previous writers:
        // SqliteBatchWriter instances are not nested on the same connection.
        UpdateConceptIdColumns(destination, seeds, conceptIds);

        return new Result(seeds.Count, translationRows, rowsByType, withReleaseId);
    }

    // ------------------------------------------------------------------
    // Reading the thirteen tables already written.
    // ------------------------------------------------------------------

    private static IReadOnlyDictionary<string, int> ReadReleaseIdByCode(SqliteConnection destination)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        using var command = destination.CreateCommand();
        command.CommandText = "SELECT \"ReleaseID\", \"ReleaseCode\" FROM \"mRelease\"";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetString(1)] = reader.GetInt32(0);
        }

        return map;
    }

    /// <summary>The sentinel domain "Open": exactly one row, DomainCode='' and DomainLabel='Open'.</summary>
    private static int FindSentinelDomainId(SqliteConnection destination)
    {
        using var command = destination.CreateCommand();
        command.CommandText = "SELECT \"DomainID\" FROM \"mDomain\" WHERE \"DomainCode\" = '' AND \"DomainLabel\" = 'Open'";
        var result = command.ExecuteScalar()
            ?? throw new InvalidOperationException(
                "Concept loader: the sentinel domain 'Open' was not found in mDomain (exactly one row "
                + "is assumed); its member cannot be excluded from mConceptTranslation.");

        return Convert.ToInt32(result);
    }

    private static List<EntitySeed> ReadReleaseSeeds(SqliteConnection destination)
    {
        var seeds = new List<EntitySeed>();
        using var command = destination.CreateCommand();
        command.CommandText = "SELECT \"ReleaseID\" FROM \"mRelease\" ORDER BY \"ReleaseID\"";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // mRelease has no label column: EmitTranslation=false, there is nothing to emit (not
            // an invented NULL — there is no row). ReleaseID is always NULL (a release does not
            // point to itself).
            seeds.Add(new EntitySeed("mRelease", "ReleaseID", reader.GetInt32(0), "Release", EmitTranslation: false, Label: null, ReleaseId: null));
        }

        return seeds;
    }

    private static List<EntitySeed> ReadReportingFrameworkSeeds(SqliteConnection destination)
        => ReadSimpleSeeds(destination, "mReportingFramework", "FrameworkID", "FrameworkLabel", "ReportingFramework");

    private static List<EntitySeed> ReadTaxonomySeeds(SqliteConnection destination, IReadOnlyDictionary<string, int> releaseIdByCode)
    {
        var seeds = new List<EntitySeed>();
        using var command = destination.CreateCommand();
        command.CommandText = "SELECT \"TaxonomyID\", \"TaxonomyLabel\", \"TaxonomyCode\" FROM \"mTaxonomy\" ORDER BY \"TaxonomyID\"";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt32(0);
            var label = reader.IsDBNull(1) ? null : reader.GetString(1); // TaxonomyLabel: always NULL, reproduced as is.
            var taxonomyCode = reader.IsDBNull(2) ? null : reader.GetString(2);
            var releaseId = ResolveReleaseFromTaxonomyCode(taxonomyCode, releaseIdByCode);
            seeds.Add(new EntitySeed("mTaxonomy", "TaxonomyID", id, "Taxonomy", EmitTranslation: true, label, releaseId));
        }

        return seeds;
    }

    /// <summary>
    /// The sentinel DOMAIN 9999 ("Open") carries NO concept (check A-SEN-01) — the reference
    /// leaves it NULL, the only such row in the whole of <c>mDomain</c> — unlike its sentinel
    /// MEMBER 9999, which does carry one (<see cref="ReadMemberSeeds"/> excludes only its
    /// TRANSLATION, not its concept). It is excluded here, not in <see cref="ReadSimpleSeeds"/>
    /// (generic over eight types without this anomaly). Note that <c>mConcept</c> has one
    /// <c>Domain</c> fewer than there are domains — it is not a gap, it is the measured asymmetry.
    /// </summary>
    private static List<EntitySeed> ReadDomainSeeds(SqliteConnection destination, int sentinelDomainId)
        => ReadSimpleSeeds(destination, "mDomain", "DomainID", "DomainLabel", "Domain")
            .Where(seed => seed.EntityId != sentinelDomainId)
            .ToList();

    private static List<EntitySeed> ReadMemberSeeds(
        SqliteConnection destination, int sentinelDomainId, IReadOnlyDictionary<string, int> releaseIdByCode)
    {
        var seeds = new List<EntitySeed>();
        using var command = destination.CreateCommand();
        command.CommandText =
            "SELECT \"MemberID\", \"MemberLabel\", \"MemberXBRLCode\", \"DomainID\" FROM \"mMember\" ORDER BY \"MemberID\"";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt32(0);
            var label = reader.IsDBNull(1) ? null : reader.GetString(1);
            var xbrlCode = reader.IsDBNull(2) ? null : reader.GetString(2);
            var domainId = reader.GetInt32(3);
            var isSentinel = domainId == sentinelDomainId; // the sentinel member "Open", without translation.
            // ReleaseID: same suffix form as Dimension — in practice always NULL because the
            // dictionary loader never composes MemberXBRLCode with a release suffix (neither for
            // MET nor for the rest).
            var releaseId = isSentinel ? null : ResolveReleaseFromColonSuffix(xbrlCode, releaseIdByCode);
            seeds.Add(new EntitySeed("mMember", "MemberID", id, "Member", EmitTranslation: !isSentinel, label, releaseId));
        }

        return seeds;
    }

    private static List<EntitySeed> ReadDimensionSeeds(SqliteConnection destination, IReadOnlyDictionary<string, int> releaseIdByCode)
    {
        var seeds = new List<EntitySeed>();
        using var command = destination.CreateCommand();
        command.CommandText =
            "SELECT \"DimensionID\", \"DimensionLabel\", \"DimensionXBRLCode\" FROM \"mDimension\" ORDER BY \"DimensionID\"";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt32(0);
            var label = reader.IsDBNull(1) ? null : reader.GetString(1);
            var xbrlCode = reader.IsDBNull(2) ? null : reader.GetString(2);
            var isMet = id == MetDimensionId; // the MET dimension, without translation.
            var releaseId = isMet ? null : ResolveReleaseFromColonSuffix(xbrlCode, releaseIdByCode);
            seeds.Add(new EntitySeed("mDimension", "DimensionID", id, "Dimension", EmitTranslation: !isMet, label, releaseId));
        }

        return seeds;
    }

    private static List<EntitySeed> ReadHierarchySeeds(SqliteConnection destination)
    {
        // ReleaseID is always NULL — the field has no referent in our output (a single row is
        // emitted per hierarchy; in the reference it labels the duplicated version).
        return ReadSimpleSeeds(destination, "mHierarchy", "HierarchyID", "HierarchyLabel", "Hierarchy");
    }

    private static List<EntitySeed> ReadHierarchyNodeSeeds(SqliteConnection destination)
        => ReadSimpleSeeds(destination, "mHierarchyNode", "HierarchyNodeID", "HierarchyNodeLabel", "HierarchyNode");

    private static List<EntitySeed> ReadAxisSeeds(SqliteConnection destination)
        => ReadSimpleSeeds(destination, "mAxis", "AxisID", "AxisLabel", "Axis");

    private static List<EntitySeed> ReadOrdinateSeeds(SqliteConnection destination)
        => ReadSimpleSeeds(destination, "mAxisOrdinate", "OrdinateID", "OrdinateLabel", "Ordinate");

    private static List<EntitySeed> ReadTemplateOrTableSeeds(SqliteConnection destination)
        => ReadSimpleSeeds(destination, "mTemplateOrTable", "TemplateOrTableID", "TemplateOrTableLabel", "TemplateOrTable");

    private static List<EntitySeed> ReadTableSeeds(SqliteConnection destination)
        => ReadSimpleSeeds(destination, "mTable", "TableID", "TableLabel", "Table");

    private static List<EntitySeed> ReadModuleSeeds(SqliteConnection destination)
        => ReadSimpleSeeds(destination, "mModule", "ModuleID", "ModuleLabel", "Module");

    /// <summary>
    /// The eight types with no possible release suffix: their key and label are read, nothing
    /// more — <c>ReleaseID</c> is always NULL, <c>EmitTranslation</c> is always true.
    /// </summary>
    private static List<EntitySeed> ReadSimpleSeeds(
        SqliteConnection destination, string tableName, string idColumn, string labelColumn, string conceptType)
    {
        var seeds = new List<EntitySeed>();
        using var command = destination.CreateCommand();
        command.CommandText = $"SELECT \"{idColumn}\", \"{labelColumn}\" FROM \"{tableName}\" ORDER BY \"{idColumn}\"";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt32(0);
            var label = reader.IsDBNull(1) ? null : reader.GetString(1);
            seeds.Add(new EntitySeed(tableName, idColumn, id, conceptType, EmitTranslation: true, label, ReleaseId: null));
        }

        return seeds;
    }

    // ------------------------------------------------------------------
    // The ReleaseID comes from the suffix of the code WE EMIT, and from nothing else.
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>Dimension</c>/<c>Member</c>: the suffix has the form <c>eba_..._{release}:code</c>
    /// (e.g. <c>eba_dim_4.0:code</c>). The segment between the last <c>_</c> before the colon and
    /// the colon itself is taken, and resolved against <c>mRelease.ReleaseCode</c>. If there is no
    /// colon, no underscore before it, or the segment does not name a known release, NULL — there
    /// is no suffix to read.
    /// </summary>
    private static int? ResolveReleaseFromColonSuffix(string? xbrlCode, IReadOnlyDictionary<string, int> releaseIdByCode)
    {
        if (string.IsNullOrEmpty(xbrlCode))
        {
            return null;
        }

        var colonIndex = xbrlCode.IndexOf(':');
        if (colonIndex < 0)
        {
            return null;
        }

        var prefix = xbrlCode[..colonIndex];
        var lastUnderscore = prefix.LastIndexOf('_');
        if (lastUnderscore < 0 || lastUnderscore == prefix.Length - 1)
        {
            return null;
        }

        var candidate = prefix[(lastUnderscore + 1)..];
        return releaseIdByCode.TryGetValue(candidate, out var releaseId) ? releaseId : null;
    }

    /// <summary>
    /// <c>Taxonomy</c>: <c>TaxonomyCode</c> has the form <c>{framework} {release}</c>
    /// (<see cref="Dpm20TaxonomyDeriver"/>, e.g. <c>corep 4.0</c>). The last token after the last
    /// space is taken and resolved against <c>mRelease.ReleaseCode</c>.
    /// </summary>
    private static int? ResolveReleaseFromTaxonomyCode(string? taxonomyCode, IReadOnlyDictionary<string, int> releaseIdByCode)
    {
        if (string.IsNullOrEmpty(taxonomyCode))
        {
            return null;
        }

        var lastSpace = taxonomyCode.LastIndexOf(' ');
        if (lastSpace < 0 || lastSpace == taxonomyCode.Length - 1)
        {
            return null;
        }

        var candidate = taxonomyCode[(lastSpace + 1)..];
        return releaseIdByCode.TryGetValue(candidate, out var releaseId) ? releaseId : null;
    }

    // ------------------------------------------------------------------
    // ConceptID update in the thirteen tables.
    // ------------------------------------------------------------------

    /// <summary>
    /// UPDATE, table by table, of the minted <c>ConceptID</c> — with commands prepared per
    /// (table, key column) and reused row by row, inside ONE explicit transaction (not a
    /// <see cref="SqliteBatchWriter"/>: the two previous ones were already closed on this same
    /// connection, so there is no nesting, but a new one is not needed either — it is UPDATE, not
    /// INSERT).
    /// </summary>
    private static void UpdateConceptIdColumns(SqliteConnection destination, IReadOnlyList<EntitySeed> seeds, IReadOnlyList<int> conceptIds)
    {
        using var transaction = destination.BeginTransaction();
        var commandByTable = new Dictionary<(string TableName, string IdColumn), SqliteCommand>();

        try
        {
            for (var i = 0; i < seeds.Count; i++)
            {
                var seed = seeds[i];
                var key = (seed.TableName, seed.IdColumnName);
                if (!commandByTable.TryGetValue(key, out var command))
                {
                    command = destination.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        $"UPDATE \"{seed.TableName}\" SET \"ConceptID\" = $conceptId WHERE \"{seed.IdColumnName}\" = $entityId";
                    command.Parameters.Add(new SqliteParameter("$conceptId", SqliteType.Integer));
                    command.Parameters.Add(new SqliteParameter("$entityId", SqliteType.Integer));
                    commandByTable[key] = command;
                }

                command.Parameters["$conceptId"].Value = conceptIds[i];
                command.Parameters["$entityId"].Value = seed.EntityId;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally
        {
            foreach (var command in commandByTable.Values)
            {
                command.Dispose();
            }
        }
    }
}
