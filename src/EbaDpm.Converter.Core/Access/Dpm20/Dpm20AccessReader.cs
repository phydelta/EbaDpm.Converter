using System.Data;
using System.Data.OleDb;

namespace EbaDpm.Converter.Core.Access.Dpm20;

/// <summary>
/// Reader of the DPM 2.0 Access database (<c>DPM2 Database_v 4_2_20251125.accdb</c> and later
/// publications), read via ACE OLEDB. Windows-only x64.
///
/// NEW pipeline, PARALLEL to <see cref="Dpm10AccessReader"/>: it does NOT implement
/// <see cref="IDpmSourceReader"/> — that interface exposes rows shaped like DPM 1.0
/// (<c>AccessDomainRow</c>, <c>AccessAxisRow</c>...) which do not exist in this source.
/// Neither that interface nor <see cref="Dpm10AccessReader"/> is touched by DPM 2.0 support.
///
/// The release cutoff lives HERE: the constructor optionally receives the cutoff release and, if
/// none is given, uses the greatest <c>ReleaseID</c> of <c>[Release]</c>.
/// <see cref="CutoffReleaseId"/> and <see cref="CutoffReleaseCode"/> expose the chosen one,
/// read-only, so that the CLI can write it to the log.
///
/// The currency predicate is applied in a single place per method:
/// <c>StartReleaseID &lt;= R AND (EndReleaseID IS NULL OR EndReleaseID &gt; R)</c>. The <c>&gt;</c>
/// is EXCLUSIVE and not negotiable: with <c>&gt;=</c> a very large number of pairs of versions of
/// the same entity appear as current at the same time. No public method of this class returns a
/// row of a versioned table without going through this filter.
///
/// Note: <c>Table</c>, <c>Property</c>, <c>Release</c>, <c>User</c>, <c>Language</c>,
/// <c>Role</c>, <c>Order</c> and <c>Domain</c> are reserved words in Access SQL, and in this
/// source several of them are also table names: always use square brackets.
///
/// Note: a JOIN of 3 or more aliased tables fails with a "Syntax error in FROM clause" in this
/// source via ACE OLEDB. A JOIN of 2 tables does work (used in <see cref="ReadTableVersions"/>).
/// The methods that would need a larger JOIN read the tables separately and combine them in
/// memory in the mapping layer (<c>Dpm20TaxonomyDeriver</c>): the volumes involved are small.
/// </summary>
public sealed class Dpm20AccessReader : IDisposable
{
    private readonly string _connectionString;
    private readonly int? _requestedCutoffReleaseId;
    private OleDbConnection? _connection;
    private bool _disposed;
    private int? _cutoffReleaseId;
    private string? _cutoffReleaseCode;

    public Dpm20AccessReader(string accdbPath, int? cutoffReleaseId = null)
    {
        if (string.IsNullOrWhiteSpace(accdbPath))
        {
            throw new ArgumentException("The .accdb file path cannot be empty.", nameof(accdbPath));
        }

        _connectionString =
            $"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={accdbPath};Persist Security Info=False;";
        _requestedCutoffReleaseId = cutoffReleaseId;
    }

    /// <summary>
    /// Cutoff <c>ReleaseID</c>. Resolved in <see cref="Open"/>: the one requested in the
    /// constructor, or if none was given, the greatest of <c>[Release]</c>.
    /// </summary>
    public int CutoffReleaseId => _cutoffReleaseId
        ?? throw new InvalidOperationException("The cutoff release is not resolved yet. Call Open() first.");

    /// <summary><c>Release.Code</c> of the cutoff release (e.g. "4.2"), for the CLI log.</summary>
    public string CutoffReleaseCode => _cutoffReleaseCode
        ?? throw new InvalidOperationException("The cutoff release is not resolved yet. Call Open() first.");

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connection is not null)
        {
            return;
        }

        _connection = new OleDbConnection(_connectionString);
        _connection.Open();

        ResolveCutoffRelease();
    }

    public void Close()
    {
        _connection?.Close();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _connection?.Dispose();
        _connection = null;
        _disposed = true;
    }

    private void ResolveCutoffRelease()
    {
        var releases = ReadReleasesInternal().ToList();
        if (releases.Count == 0)
        {
            throw new InvalidOperationException("The [Release] table of the DPM 2.0 Access database is empty; the cutoff release cannot be resolved.");
        }

        int cutoffId;
        if (_requestedCutoffReleaseId is { } requested)
        {
            cutoffId = requested;
        }
        else
        {
            cutoffId = releases.Max(r => r.ReleaseId);
        }

        var cutoffRow = releases.FirstOrDefault(r => r.ReleaseId == cutoffId)
            ?? throw new InvalidOperationException(
                $"The requested cutoff release (ReleaseID={cutoffId}) does not exist in [Release]. "
                + $"Available releases: {string.Join(", ", releases.Select(r => $"{r.ReleaseId}={r.Code}"))}.");

        _cutoffReleaseId = cutoffRow.ReleaseId;
        _cutoffReleaseCode = cutoffRow.Code;
    }

    // The currency predicate is applied in SQL, in the query of each filtered method
    // (ReadModuleVersions, ReadTableVersions): "Start <= R AND (End IS NULL OR End > R)", with
    // the EXCLUSIVE ">". A single place per method, no unfiltered shortcut.

    // ------------------------------------------------------------------
    // Generic streaming read (same pattern as Dpm10AccessReader)
    // ------------------------------------------------------------------

    private IEnumerable<IDataRecord> Query(string sql, IReadOnlyList<object>? parameters = null)
    {
        EnsureOpen();

        using var command = new OleDbCommand(sql, _connection);
        if (parameters is not null)
        {
            foreach (var value in parameters)
            {
                command.Parameters.Add(new OleDbParameter { Value = value });
            }
        }

        using var reader = command.ExecuteReader(CommandBehavior.SequentialAccess);
        while (reader.Read())
        {
            yield return reader;
        }
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connection is null)
        {
            throw new InvalidOperationException(
                "The DPM 2.0 Access connection is not open. Call Open() before reading.");
        }
    }

    // ------------------------------------------------------------------
    // Typed reads — mMetric, mDimension. None is filtered by the currency predicate, same
    // criterion as the rest of the dictionary.
    // ------------------------------------------------------------------

    /// <summary>Reads the whole <c>[DataType]</c> table. Not versioned.</summary>
    public IEnumerable<Dpm20DataTypeRow> ReadDataTypes()
    {
        const string sql = "SELECT [DataTypeID], [Code] FROM [DataType]";

        foreach (var row in Query(sql))
        {
            var dataTypeId = Convert.ToInt32(row.GetValue(0));
            var code = GetNullableString(row, 1)
                ?? throw new InvalidOperationException($"DataType has no Code (DataTypeID={dataTypeId}).");

            yield return new Dpm20DataTypeRow(dataTypeId, code);
        }
    }

    /// <summary>
    /// Reads the whole <c>Property</c> table: only <c>PropertyID</c> and <c>DataTypeID</c>, which
    /// is what <c>mMetric.DataType</c> and the <c>_NA</c> split of <c>mDimension</c> need.
    /// </summary>
    public IEnumerable<Dpm20PropertyRow> ReadProperties()
    {
        const string sql = "SELECT [PropertyID], [DataTypeID] FROM [Property]";

        foreach (var row in Query(sql))
        {
            yield return new Dpm20PropertyRow(Convert.ToInt32(row.GetValue(0)), GetNullableInt32(row, 1));
        }
    }

    /// <summary>
    /// Reads the whole <c>PropertyCategory</c> table: the domain OF THE VALUES of a property,
    /// source of <c>mDimension.DomainID</c>. Do NOT confuse it with <c>ItemCategory</c>
    /// (membership in <c>_PR</c>): its rows never include <c>_PR</c>.
    /// </summary>
    public IEnumerable<Dpm20PropertyCategoryRow> ReadPropertyCategories()
    {
        const string sql = "SELECT [PropertyID], [CategoryID], [StartReleaseID], [EndReleaseID] FROM [PropertyCategory]";

        foreach (var row in Query(sql))
        {
            yield return new Dpm20PropertyCategoryRow(
                Convert.ToInt32(row.GetValue(0)),
                Convert.ToInt32(row.GetValue(1)),
                Convert.ToInt32(row.GetValue(2)),
                GetNullableInt32(row, 3));
        }
    }

    /// <summary>
    /// <c>PropertyID</c> of the properties that appear in <c>ContextComposition</c>: first half of
    /// the identification of a dimension.
    /// </summary>
    public IEnumerable<int> ReadContextCompositionPropertyIds()
    {
        const string sql = "SELECT DISTINCT [PropertyID] FROM [ContextComposition]";

        foreach (var row in Query(sql))
        {
            yield return Convert.ToInt32(row.GetValue(0));
        }
    }

    /// <summary>
    /// <c>PropertyID</c> of the variables of type <c>key</c>: <c>VariableVersion.PropertyID</c>
    /// for <c>Variable.Type = 'key'</c>. Second half of the identification of a dimension.
    /// </summary>
    public IEnumerable<int> ReadKeyVariablePropertyIds()
    {
        const string sql =
            """
            SELECT DISTINCT vv.[PropertyID]
            FROM [VariableVersion] vv INNER JOIN [Variable] v ON v.[VariableID] = vv.[VariableID]
            WHERE v.[Type] = 'key' AND vv.[PropertyID] IS NOT NULL
            """;

        foreach (var row in Query(sql))
        {
            yield return Convert.ToInt32(row.GetValue(0));
        }
    }

    /// <summary>
    /// <c>PropertyID</c> to <c>SubCategoryID</c> via <c>HeaderVersion.SubCategoryVID</c> to
    /// <c>SubCategoryVersion.SubCategoryID</c>: source of <c>mMetric.ReferencedHierarchyID</c>.
    /// Ordered by <c>HeaderVID</c> so that the resolution of the properties with more than one
    /// distinct candidate is deterministic (the first one is kept, see
    /// <see cref="Dpm20DictionaryLoader"/>).
    /// </summary>
    public IEnumerable<Dpm20HeaderVersionSubCategoryRow> ReadHeaderVersionSubCategories()
    {
        const string sql =
            """
            SELECT hv.[PropertyID], scv.[SubCategoryID], hv.[HeaderVID]
            FROM [HeaderVersion] hv INNER JOIN [SubCategoryVersion] scv ON scv.[SubCategoryVID] = hv.[SubCategoryVID]
            WHERE hv.[PropertyID] IS NOT NULL AND hv.[SubCategoryVID] IS NOT NULL
            ORDER BY hv.[HeaderVID]
            """;

        foreach (var row in Query(sql))
        {
            yield return new Dpm20HeaderVersionSubCategoryRow(
                Convert.ToInt32(row.GetValue(0)), Convert.ToInt32(row.GetValue(1)), Convert.ToInt32(row.GetValue(2)));
        }
    }

    // ------------------------------------------------------------------
    // Typed reads — releases, frameworks, modules and table versions
    // ------------------------------------------------------------------

    /// <summary>Reads the whole <c>[Release]</c> table, unfiltered: it is not versioned.</summary>
    public IEnumerable<Dpm20ReleaseRow> ReadReleases() => ReadReleasesInternal();

    /// <summary>
    /// Internal read that does not depend on <see cref="CutoffReleaseId"/> (which may not be
    /// resolved yet the first time it is called, from <see cref="ResolveCutoffRelease"/>).
    /// </summary>
    private IEnumerable<Dpm20ReleaseRow> ReadReleasesInternal()
    {
        // Date/Status/Description are read here because the mapping (Dpm20SkeletonLoader) needs
        // them for mRelease.PublicationDate/Status/ReleaseDescription (the source provides them
        // and a third-party reference export leaves them empty). IsCurrent is read for row
        // completeness; see the doc of Dpm20ReleaseRow for how the mapping uses it.
        const string sql = "SELECT [ReleaseID], [Code], [Date], [Status], [Description], [IsCurrent] FROM [Release]";

        foreach (var row in Query(sql))
        {
            var releaseId = Convert.ToInt32(row.GetValue(0));
            var code = GetNullableString(row, 1)
                ?? throw new InvalidOperationException($"[Release] has no Code (ReleaseID={releaseId}).");
            var date = GetNullableString(row, 2);
            var status = GetNullableString(row, 3);
            var description = GetNullableString(row, 4);
            // IsCurrent arrives as -1/0 (Access boolean), same pattern as IsAbstract.
            var isCurrent = Convert.ToBoolean(row.GetValue(5));

            yield return new Dpm20ReleaseRow(releaseId, code, date, status, description, isCurrent);
        }
    }

    /// <summary>Reads the whole <c>Framework</c> table, unfiltered: it is not versioned.</summary>
    public IEnumerable<Dpm20FrameworkRow> ReadFrameworks()
    {
        const string sql = "SELECT [FrameworkID], [Code], [Name] FROM [Framework]";

        foreach (var row in Query(sql))
        {
            var frameworkId = Convert.ToInt32(row.GetValue(0));
            var code = GetNullableString(row, 1)
                ?? throw new InvalidOperationException($"Framework has no Code (FrameworkID={frameworkId}).");
            var name = GetNullableString(row, 2)
                ?? throw new InvalidOperationException($"Framework has no Name (FrameworkID={frameworkId}).");

            yield return new Dpm20FrameworkRow(frameworkId, code, name);
        }
    }

    /// <summary>Reads the whole <c>Organisation</c> table, unfiltered: it is not versioned.</summary>
    public IEnumerable<Dpm20OrganisationRow> ReadOrganisations()
    {
        const string sql = "SELECT [OrgID], [Name], [Acronym], [IDPrefix] FROM [Organisation]";

        foreach (var row in Query(sql))
        {
            var orgId = Convert.ToInt32(row.GetValue(0));
            var name = GetNullableString(row, 1)
                ?? throw new InvalidOperationException($"Organisation has no Name (OrgID={orgId}).");
            var acronym = GetNullableString(row, 2);
            var idPrefix = GetNullableInt32(row, 3);

            yield return new Dpm20OrganisationRow(orgId, name, acronym, idPrefix);
        }
    }

    /// <summary>
    /// Reads the whole <c>[Module]</c> table, unfiltered: it is not versioned.
    ///
    /// <c>isDocumentModule</c> matters: without excluding it, <c>mModule</c> would get two extra
    /// rows, <c>P3_NONREM_DIS_DOCS</c> and <c>P3_REM_DIS_DOCS</c> (<c>PILLAR3</c>), which are
    /// modules that link to PDFs and have no table at all (<c>ModuleVersionComposition</c> is
    /// empty for both). <c>isDocumentModule</c> is true for exactly those two of the current
    /// modules other than DORA. It arrives as -1/0 (Access boolean).
    /// </summary>
    public IEnumerable<Dpm20ModuleRow> ReadModules()
    {
        const string sql = "SELECT [ModuleID], [FrameworkID], [isDocumentModule] FROM [Module]";

        foreach (var row in Query(sql))
        {
            yield return new Dpm20ModuleRow(
                ModuleId: Convert.ToInt32(row.GetValue(0)),
                FrameworkId: Convert.ToInt32(row.GetValue(1)),
                IsDocumentModule: Convert.ToBoolean(row.GetValue(2)));
        }
    }

    /// <summary>
    /// Reads <c>ModuleVersion</c> filtered by the currency predicate against
    /// <see cref="CutoffReleaseId"/>. Versioned table: never without this filter.
    /// </summary>
    public IEnumerable<Dpm20ModuleVersionRow> ReadModuleVersions()
    {
        var cutoff = CutoffReleaseId;
        const string sql =
            """
            SELECT [ModuleVID], [ModuleID], [Code], [Name], [StartReleaseID], [EndReleaseID]
            FROM [ModuleVersion]
            WHERE [StartReleaseID] <= ? AND ([EndReleaseID] IS NULL OR [EndReleaseID] > ?)
            """;

        foreach (var row in Query(sql, [cutoff, cutoff]))
        {
            var moduleVId = Convert.ToInt32(row.GetValue(0));
            var moduleId = Convert.ToInt32(row.GetValue(1));
            var code = GetNullableString(row, 2)
                ?? throw new InvalidOperationException($"ModuleVersion has no Code (ModuleVID={moduleVId}).");
            var name = GetNullableString(row, 3)
                ?? throw new InvalidOperationException($"ModuleVersion has no Name (ModuleVID={moduleVId}).");
            var startReleaseId = Convert.ToInt32(row.GetValue(4));
            var endReleaseId = GetNullableInt32(row, 5);

            yield return new Dpm20ModuleVersionRow(moduleVId, moduleId, code, name, startReleaseId, endReleaseId);
        }
    }

    /// <summary>
    /// Reads the whole <c>ModuleVersionComposition</c> table, unfiltered: the table itself is not
    /// versioned — the currency of each pair is decided by <c>ModuleVID</c> and <c>TableVID</c>
    /// separately, each via its own versioned table.
    /// </summary>
    public IEnumerable<Dpm20ModuleVersionCompositionRow> ReadModuleVersionCompositions()
    {
        const string sql = "SELECT [ModuleVID], [TableVID] FROM [ModuleVersionComposition]";

        foreach (var row in Query(sql))
        {
            yield return new Dpm20ModuleVersionCompositionRow(
                ModuleVId: Convert.ToInt32(row.GetValue(0)),
                TableVId: Convert.ToInt32(row.GetValue(1)));
        }
    }

    /// <summary>
    /// Reads <c>TableVersion</c> joined with <c>[Table]</c> (a 2-table JOIN, which works in this
    /// source), filtered by the currency predicate against <see cref="CutoffReleaseId"/>.
    /// Versioned table: never without this filter.
    ///
    /// Returns the abstract versions included: the <c>IsAbstract</c> filter does NOT belong here,
    /// it is the mapping's responsibility, target table by target table.
    /// </summary>
    public IEnumerable<Dpm20TableVersionRow> ReadTableVersions()
    {
        var cutoff = CutoffReleaseId;
        const string sql =
            """
            SELECT tv.[TableVID], tv.[Code], tv.[Name], tv.[TableID], tv.[AbstractTableID],
                   tv.[StartReleaseID], tv.[EndReleaseID], t.[IsAbstract], tv.[ContextID]
            FROM [TableVersion] tv INNER JOIN [Table] t ON t.[TableID] = tv.[TableID]
            WHERE tv.[StartReleaseID] <= ? AND (tv.[EndReleaseID] IS NULL OR tv.[EndReleaseID] > ?)
            """;

        foreach (var row in Query(sql, [cutoff, cutoff]))
        {
            var tableVId = Convert.ToInt32(row.GetValue(0));
            var code = GetNullableString(row, 1)
                ?? throw new InvalidOperationException($"TableVersion has no Code (TableVID={tableVId}).");
            var name = GetNullableString(row, 2)
                ?? throw new InvalidOperationException($"TableVersion has no Name (TableVID={tableVId}).");
            var tableId = Convert.ToInt32(row.GetValue(3));
            var abstractTableId = GetNullableInt32(row, 4);
            var startReleaseId = Convert.ToInt32(row.GetValue(5));
            var endReleaseId = GetNullableInt32(row, 6);
            // IsAbstract arrives as -1/0 (Access boolean).
            var isAbstract = Convert.ToBoolean(row.GetValue(7));
            var contextId = GetNullableInt32(row, 8);

            yield return new Dpm20TableVersionRow(
                tableVId, code, name, tableId, abstractTableId, startReleaseId, endReleaseId, isAbstract, contextId);
        }
    }

    // ------------------------------------------------------------------
    // Typed reads — mDomain, mDomainUnion, mMember, mHierarchy.
    // None of these tables is filtered by the currency predicate: the dictionary is shared
    // across taxonomies and is not pruned by release (same criterion as DPM 1.0's
    // DictionaryLoader). The counts that match the reference come from ItemCategory,
    // SuperCategoryComposition and SubCategory WITHOUT applying StartReleaseID/EndReleaseID.
    // ------------------------------------------------------------------

    /// <summary>Reads the whole <c>Category</c> table. Not versioned.</summary>
    public IEnumerable<Dpm20CategoryRow> ReadCategories()
    {
        const string sql = "SELECT [CategoryID], [Code], [Name], [Description], [IsEnumerated] FROM [Category]";

        foreach (var row in Query(sql))
        {
            var categoryId = Convert.ToInt32(row.GetValue(0));
            var code = GetNullableString(row, 1)
                ?? throw new InvalidOperationException($"Category has no Code (CategoryID={categoryId}).");
            var name = GetNullableString(row, 2);
            var description = GetNullableString(row, 3);
            // IsEnumerated arrives as -1/0 (Access boolean).
            var isEnumerated = Convert.ToBoolean(row.GetValue(4));

            yield return new Dpm20CategoryRow(categoryId, code, name, description, isEnumerated);
        }
    }

    /// <summary>
    /// Reads the whole <c>ItemCategory</c> table: the item-to-domain membership, not filtered by
    /// the currency predicate (the target emits the complete history of codes, including the
    /// renamed <c>old-*</c> ones).
    /// </summary>
    public IEnumerable<Dpm20ItemCategoryRow> ReadItemCategories()
    {
        const string sql =
            "SELECT [ItemID], [CategoryID], [Code], [IsDefaultItem], [StartReleaseID], [EndReleaseID] FROM [ItemCategory]";

        foreach (var row in Query(sql))
        {
            var itemId = Convert.ToInt32(row.GetValue(0));
            var categoryId = Convert.ToInt32(row.GetValue(1));
            var code = GetNullableString(row, 2)
                ?? throw new InvalidOperationException(
                    $"ItemCategory has no Code (ItemID={itemId}, CategoryID={categoryId}).");
            // IsDefaultItem arrives as -1/0 (Access boolean).
            var isDefaultItem = Convert.ToBoolean(row.GetValue(3));
            var startReleaseId = Convert.ToInt32(row.GetValue(4));
            var endReleaseId = GetNullableInt32(row, 5);

            yield return new Dpm20ItemCategoryRow(itemId, categoryId, code, isDefaultItem, startReleaseId, endReleaseId);
        }
    }

    /// <summary>Reads the whole <c>Item</c> table: the name for <c>mMember.MemberLabel</c>.</summary>
    public IEnumerable<Dpm20ItemRow> ReadItems()
    {
        const string sql = "SELECT [ItemID], [Name] FROM [Item]";

        foreach (var row in Query(sql))
        {
            yield return new Dpm20ItemRow(Convert.ToInt32(row.GetValue(0)), GetNullableString(row, 1));
        }
    }

    /// <summary>
    /// Reads the whole <c>SuperCategoryComposition</c> table: <c>mDomainUnion</c>, direct
    /// translation — the union is NOT materialized, this table only declares the pairs.
    /// </summary>
    public IEnumerable<Dpm20SuperCategoryCompositionRow> ReadSuperCategoryCompositions()
    {
        const string sql = "SELECT [SuperCategoryID], [CategoryID] FROM [SuperCategoryComposition]";

        foreach (var row in Query(sql))
        {
            yield return new Dpm20SuperCategoryCompositionRow(
                Convert.ToInt32(row.GetValue(0)),
                Convert.ToInt32(row.GetValue(1)));
        }
    }

    /// <summary>
    /// Reads the whole <c>SubCategory</c> table: <c>mHierarchy</c>, 1 to 1. It includes some rows
    /// whose <c>CategoryID</c> is that of <c>_PR</c> (<c>AT*</c> codes, hierarchies of the metric
    /// domain in DPM 1.0 terminology): the mapping assigns them to the <c>MET</c> domain, it does
    /// not discard them.
    /// </summary>
    public IEnumerable<Dpm20SubCategoryRow> ReadSubCategories()
    {
        const string sql = "SELECT [SubCategoryID], [CategoryID], [Code], [Name] FROM [SubCategory]";

        foreach (var row in Query(sql))
        {
            // SequentialAccess requires reading in increasing ordinal order: CategoryID (1) before
            // Code (2), even though Code is needed earlier when building the record.
            var subCategoryId = Convert.ToInt32(row.GetValue(0));
            var categoryId = Convert.ToInt32(row.GetValue(1));
            var code = GetNullableString(row, 2)
                ?? throw new InvalidOperationException($"SubCategory has no Code (SubCategoryID={subCategoryId}).");
            var name = GetNullableString(row, 3);

            yield return new Dpm20SubCategoryRow(subCategoryId, categoryId, code, name);
        }
    }

    /// <summary>
    /// <c>PropertyID</c> (= <c>ItemID</c>) of the variables with role <c>fact</c>:
    /// <c>VariableVersion.PropertyID</c> for <c>Variable.Type = 'fact'</c>. 2-table JOIN (the only
    /// size that works in this source, see the class header). First of the two structural sources
    /// for identifying a metric: it is used here ONLY to decide which <c>_PR</c> codes enter the
    /// <c>MET</c> domain of <c>mMember</c> — it does NOT build <c>mMetric</c>. No currency
    /// filter, like the rest of the dictionary.
    /// </summary>
    public IEnumerable<int> ReadFactVariablePropertyIds()
    {
        const string sql =
            """
            SELECT DISTINCT vv.[PropertyID]
            FROM [VariableVersion] vv INNER JOIN [Variable] v ON v.[VariableID] = vv.[VariableID]
            WHERE v.[Type] = 'fact' AND vv.[PropertyID] IS NOT NULL
            """;

        foreach (var row in Query(sql))
        {
            yield return Convert.ToInt32(row.GetValue(0));
        }
    }

    /// <summary>
    /// <c>PropertyID</c> of the NON-key headers: <c>HeaderVersion.PropertyID</c> for
    /// <c>Header.IsKey = false</c>. Second source for identifying a metric. Same use and same
    /// caveats as <see cref="ReadFactVariablePropertyIds"/>.
    /// </summary>
    public IEnumerable<int> ReadNonKeyHeaderPropertyIds()
    {
        const string sql =
            """
            SELECT DISTINCT hv.[PropertyID]
            FROM [HeaderVersion] hv INNER JOIN [Header] h ON h.[HeaderID] = hv.[HeaderID]
            WHERE h.[IsKey] = 0 AND hv.[PropertyID] IS NOT NULL
            """;

        foreach (var row in Query(sql))
        {
            yield return Convert.ToInt32(row.GetValue(0));
        }
    }

    // ------------------------------------------------------------------
    // Typed reads — mTable, mTaxonomyTable, mTemplateOrTable.
    // ------------------------------------------------------------------

    /// <summary>
    /// Reads <c>TableGroup</c> filtered by the currency predicate. Versioned table: never without
    /// this filter.
    /// </summary>
    public IEnumerable<Dpm20TableGroupRow> ReadTableGroups()
    {
        var cutoff = CutoffReleaseId;
        const string sql =
            """
            SELECT [TableGroupID], [Code], [Name], [Type], [StartReleaseID], [EndReleaseID]
            FROM [TableGroup]
            WHERE [StartReleaseID] <= ? AND ([EndReleaseID] IS NULL OR [EndReleaseID] > ?)
            """;

        foreach (var row in Query(sql, [cutoff, cutoff]))
        {
            var tableGroupId = Convert.ToInt32(row.GetValue(0));
            var code = GetNullableString(row, 1)
                ?? throw new InvalidOperationException($"TableGroup has no Code (TableGroupID={tableGroupId}).");
            var name = GetNullableString(row, 2);
            var type = GetNullableString(row, 3);
            var startReleaseId = Convert.ToInt32(row.GetValue(4));
            var endReleaseId = GetNullableInt32(row, 5);

            yield return new Dpm20TableGroupRow(tableGroupId, code, name, type, startReleaseId, endReleaseId);
        }
    }

    /// <summary>
    /// Reads <c>TableGroupComposition</c> filtered by the currency predicate: the (group, concrete
    /// table) pair. Versioned table (it carries its own <c>StartReleaseID</c>/<c>EndReleaseID</c>,
    /// unlike <c>ModuleVersionComposition</c>): never without this filter.
    /// </summary>
    public IEnumerable<Dpm20TableGroupCompositionRow> ReadTableGroupCompositions()
    {
        var cutoff = CutoffReleaseId;
        const string sql =
            """
            SELECT [TableGroupID], [TableID]
            FROM [TableGroupComposition]
            WHERE [StartReleaseID] <= ? AND ([EndReleaseID] IS NULL OR [EndReleaseID] > ?)
            """;

        foreach (var row in Query(sql, [cutoff, cutoff]))
        {
            yield return new Dpm20TableGroupCompositionRow(
                Convert.ToInt32(row.GetValue(0)), Convert.ToInt32(row.GetValue(1)));
        }
    }

    // ------------------------------------------------------------------
    // Typed reads — axes, ordinates and cells. None filters by the currency predicate: they
    // already arrive bounded to the TableVID/TableID/HeaderVID/ContextID of the selected tables
    // (same 500-item batching pattern as Dpm10AccessReader.ReadTableVersionsByIds).
    // ------------------------------------------------------------------

    private const int IdListChunkSize = 500;

    /// <summary>
    /// Reads <c>TableVersionHeader</c> filtered by <c>TableVID</c>: the header tree of each table
    /// version.
    /// </summary>
    public IEnumerable<Dpm20TableVersionHeaderRow> ReadTableVersionHeaders(IEnumerable<int> tableVIds)
    {
        ArgumentNullException.ThrowIfNull(tableVIds);

        var distinctIds = tableVIds.Distinct().ToList();
        for (var offset = 0; offset < distinctIds.Count; offset += IdListChunkSize)
        {
            var chunk = distinctIds.Skip(offset).Take(IdListChunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select(_ => "?"));
            var sql =
                $"""
                SELECT [TableVID], [HeaderID], [HeaderVID], [ParentHeaderID], [Order], [IsAbstract]
                FROM [TableVersionHeader]
                WHERE [TableVID] IN ({placeholders})
                """;

            foreach (var row in Query(sql, chunk.Cast<object>().ToList()))
            {
                yield return new Dpm20TableVersionHeaderRow(
                    TableVId: Convert.ToInt32(row.GetValue(0)),
                    HeaderId: Convert.ToInt32(row.GetValue(1)),
                    HeaderVId: Convert.ToInt32(row.GetValue(2)),
                    ParentHeaderId: GetNullableInt32(row, 3),
                    Order: Convert.ToInt32(row.GetValue(4)),
                    IsAbstract: Convert.ToBoolean(row.GetValue(5)));
            }
        }
    }

    /// <summary>
    /// Reads <c>Header</c> filtered by <c>TableID</c> (the ENTITY, NEVER a <c>TableVID</c>).
    /// </summary>
    public IEnumerable<Dpm20HeaderRow> ReadHeadersByTableIds(IEnumerable<int> tableIds)
    {
        ArgumentNullException.ThrowIfNull(tableIds);

        var distinctIds = tableIds.Distinct().ToList();
        for (var offset = 0; offset < distinctIds.Count; offset += IdListChunkSize)
        {
            var chunk = distinctIds.Skip(offset).Take(IdListChunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select(_ => "?"));
            var sql =
                $"""
                SELECT [HeaderID], [TableID], [Direction], [IsKey]
                FROM [Header]
                WHERE [TableID] IN ({placeholders})
                """;

            foreach (var row in Query(sql, chunk.Cast<object>().ToList()))
            {
                var headerId = Convert.ToInt32(row.GetValue(0));
                var tableId = Convert.ToInt32(row.GetValue(1));
                var direction = GetNullableString(row, 2)
                    ?? throw new InvalidOperationException($"Header has no Direction (HeaderID={headerId}).");

                yield return new Dpm20HeaderRow(headerId, tableId, direction, Convert.ToBoolean(row.GetValue(3)));
            }
        }
    }

    /// <summary>
    /// Reads <c>HeaderVersion</c> filtered by <c>HeaderVID</c> — those already resolved by
    /// <see cref="ReadTableVersionHeaders"/>, not the whole table.
    /// </summary>
    public IEnumerable<Dpm20HeaderVersionRow> ReadHeaderVersionsByIds(IEnumerable<int> headerVIds)
    {
        ArgumentNullException.ThrowIfNull(headerVIds);

        var distinctIds = headerVIds.Distinct().ToList();
        for (var offset = 0; offset < distinctIds.Count; offset += IdListChunkSize)
        {
            var chunk = distinctIds.Skip(offset).Take(IdListChunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select(_ => "?"));
            var sql =
                $"""
                SELECT [HeaderVID], [HeaderID], [Code], [Label], [PropertyID], [ContextID], [SubCategoryVID]
                FROM [HeaderVersion]
                WHERE [HeaderVID] IN ({placeholders})
                """;

            foreach (var row in Query(sql, chunk.Cast<object>().ToList()))
            {
                yield return new Dpm20HeaderVersionRow(
                    HeaderVId: Convert.ToInt32(row.GetValue(0)),
                    HeaderId: Convert.ToInt32(row.GetValue(1)),
                    Code: GetNullableString(row, 2),
                    Label: GetNullableString(row, 3),
                    PropertyId: GetNullableInt32(row, 4),
                    ContextId: GetNullableInt32(row, 5),
                    SubCategoryVId: GetNullableInt32(row, 6));
            }
        }
    }

    /// <summary>
    /// Reads <c>ContextComposition</c> filtered by <c>ContextID</c> — the largest table of the
    /// source (over 1.7 million rows), which is why it is NEVER read without this filter: only
    /// the contexts of the headers reached by the current selection.
    /// </summary>
    public IEnumerable<Dpm20ContextCompositionRow> ReadContextCompositionsByContextIds(IEnumerable<int> contextIds)
    {
        ArgumentNullException.ThrowIfNull(contextIds);

        var distinctIds = contextIds.Distinct().ToList();
        for (var offset = 0; offset < distinctIds.Count; offset += IdListChunkSize)
        {
            var chunk = distinctIds.Skip(offset).Take(IdListChunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select(_ => "?"));
            var sql =
                $"""
                SELECT [ContextID], [PropertyID], [ItemID]
                FROM [ContextComposition]
                WHERE [ContextID] IN ({placeholders})
                """;

            foreach (var row in Query(sql, chunk.Cast<object>().ToList()))
            {
                yield return new Dpm20ContextCompositionRow(
                    Convert.ToInt32(row.GetValue(0)), Convert.ToInt32(row.GetValue(1)), Convert.ToInt32(row.GetValue(2)));
            }
        }
    }

    /// <summary>
    /// Reads <c>Cell</c> filtered by <c>TableID</c> (the ENTITY): the grid that the source DOES
    /// provide, before the transposition step and the expansion to the full grid.
    /// </summary>
    public IEnumerable<Dpm20CellRow> ReadCellsByTableIds(IEnumerable<int> tableIds)
    {
        ArgumentNullException.ThrowIfNull(tableIds);

        var distinctIds = tableIds.Distinct().ToList();
        for (var offset = 0; offset < distinctIds.Count; offset += IdListChunkSize)
        {
            var chunk = distinctIds.Skip(offset).Take(IdListChunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select(_ => "?"));
            var sql =
                $"""
                SELECT [CellID], [TableID], [ColumnID], [RowID], [SheetID]
                FROM [Cell]
                WHERE [TableID] IN ({placeholders})
                """;

            foreach (var row in Query(sql, chunk.Cast<object>().ToList()))
            {
                yield return new Dpm20CellRow(
                    Convert.ToInt32(row.GetValue(0)),
                    Convert.ToInt32(row.GetValue(1)),
                    GetNullableInt32(row, 2),
                    GetNullableInt32(row, 3),
                    GetNullableInt32(row, 4));
            }
        }
    }

    /// <summary>
    /// Reads <c>TableVersionCell</c> filtered by <c>TableVID</c>: <c>IsExcluded</c>/<c>IsVoid</c> of
    /// each cell IN THIS table version, and <c>VariableVID</c>: the cell-to-variable link, NULL
    /// when the cell does not result in a variable (<c>IsVoid</c>, see the DPM 2.0 metamodel
    /// documentation).
    /// </summary>
    public IEnumerable<Dpm20TableVersionCellRow> ReadTableVersionCellsByTableVIds(IEnumerable<int> tableVIds)
    {
        ArgumentNullException.ThrowIfNull(tableVIds);

        var distinctIds = tableVIds.Distinct().ToList();
        for (var offset = 0; offset < distinctIds.Count; offset += IdListChunkSize)
        {
            var chunk = distinctIds.Skip(offset).Take(IdListChunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select(_ => "?"));
            var sql =
                $"""
                SELECT [TableVID], [CellID], [IsExcluded], [IsVoid], [VariableVID]
                FROM [TableVersionCell]
                WHERE [TableVID] IN ({placeholders})
                """;

            foreach (var row in Query(sql, chunk.Cast<object>().ToList()))
            {
                yield return new Dpm20TableVersionCellRow(
                    Convert.ToInt32(row.GetValue(0)),
                    Convert.ToInt32(row.GetValue(1)),
                    Convert.ToBoolean(row.GetValue(2)),
                    Convert.ToBoolean(row.GetValue(3)),
                    GetNullableInt32(row, 4));
            }
        }
    }

    /// <summary>
    /// Reads <c>VariableVersion</c> filtered by <c>VariableVID</c>: <c>ContextID</c> — the ALREADY
    /// RESOLVED context of the data point, source of <c>mOrdinateCategorisation</c> projected onto
    /// the ordinates. The <c>PropertyID</c> (the metric) is NOT read here (it keeps coming from the
    /// header, unchanged).
    /// </summary>
    public IEnumerable<Dpm20VariableVersionRow> ReadVariableVersionsByVariableVIds(IEnumerable<int> variableVIds)
    {
        ArgumentNullException.ThrowIfNull(variableVIds);

        var distinctIds = variableVIds.Distinct().ToList();
        for (var offset = 0; offset < distinctIds.Count; offset += IdListChunkSize)
        {
            var chunk = distinctIds.Skip(offset).Take(IdListChunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select(_ => "?"));
            var sql =
                $"""
                SELECT [VariableVID], [ContextID]
                FROM [VariableVersion]
                WHERE [VariableVID] IN ({placeholders})
                """;

            foreach (var row in Query(sql, chunk.Cast<object>().ToList()))
            {
                yield return new Dpm20VariableVersionRow(
                    Convert.ToInt32(row.GetValue(0)),
                    GetNullableInt32(row, 1));
            }
        }
    }

    /// <summary>
    /// Reads <c>SubCategoryVersion</c> filtered by the currency predicate against
    /// <see cref="CutoffReleaseId"/>: <c>SubCategoryVID</c> to <c>SubCategoryID</c>, the hierarchy
    /// restriction of an open axis — the same <c>SubCategoryVID</c> that already travels in
    /// <see cref="Dpm20HeaderVersionRow.SubCategoryVId"/>. Versioned table: never without this
    /// filter.
    /// </summary>
    public IEnumerable<Dpm20SubCategoryVersionRow> ReadSubCategoryVersions()
    {
        var cutoff = CutoffReleaseId;
        const string sql =
            """
            SELECT [SubCategoryVID], [SubCategoryID], [StartReleaseID], [EndReleaseID]
            FROM [SubCategoryVersion]
            WHERE [StartReleaseID] <= ? AND ([EndReleaseID] IS NULL OR [EndReleaseID] > ?)
            """;

        foreach (var row in Query(sql, [cutoff, cutoff]))
        {
            yield return new Dpm20SubCategoryVersionRow(
                Convert.ToInt32(row.GetValue(0)),
                Convert.ToInt32(row.GetValue(1)),
                Convert.ToInt32(row.GetValue(2)),
                GetNullableInt32(row, 3));
        }
    }

    /// <summary>
    /// Reads the whole <c>SubCategoryVersion</c> table WITHOUT the currency filter: the hierarchy
    /// node loader also needs the NON-current versions, for the subcategories that have no current
    /// version at all (their latest version is used instead).
    /// <see cref="ReadSubCategoryVersions"/> does not serve this purpose — it applies the release
    /// cutoff and discards them.
    /// </summary>
    public IEnumerable<Dpm20SubCategoryVersionRow> ReadAllSubCategoryVersions()
    {
        const string sql = "SELECT [SubCategoryVID], [SubCategoryID], [StartReleaseID], [EndReleaseID] FROM [SubCategoryVersion]";

        foreach (var row in Query(sql))
        {
            yield return new Dpm20SubCategoryVersionRow(
                Convert.ToInt32(row.GetValue(0)),
                Convert.ToInt32(row.GetValue(1)),
                Convert.ToInt32(row.GetValue(2)),
                GetNullableInt32(row, 3));
        }
    }

    /// <summary>
    /// Reads the whole <c>SubCategoryItem</c> table, UNFILTERED: the hierarchy node loader groups by
    /// <c>SubCategoryVID</c> and keeps only the rows of the EFFECTIVE version of each subcategory
    /// (the current one, or the latest one when there is none). <c>[Order]</c> is a reserved word
    /// in Access SQL: use square brackets.
    /// </summary>
    public IEnumerable<Dpm20SubCategoryItemRow> ReadSubCategoryItems()
    {
        const string sql =
            """
            SELECT [ItemID], [SubCategoryVID], [Order], [Label], [ParentItemID], [ComparisonOperatorID], [ArithmeticOperatorID]
            FROM [SubCategoryItem]
            ORDER BY [SubCategoryVID], [ItemID]
            """;

        foreach (var row in Query(sql))
        {
            yield return new Dpm20SubCategoryItemRow(
                Convert.ToInt32(row.GetValue(0)),
                Convert.ToInt32(row.GetValue(1)),
                GetNullableInt32(row, 2),
                GetNullableString(row, 3),
                GetNullableInt32(row, 4),
                GetNullableInt32(row, 5),
                GetNullableInt32(row, 6));
        }
    }

    /// <summary>Reads the whole <c>Operator</c> table. Not versioned.</summary>
    public IEnumerable<Dpm20OperatorRow> ReadOperators()
    {
        const string sql = "SELECT [OperatorID], [Symbol] FROM [Operator]";

        foreach (var row in Query(sql))
        {
            var operatorId = Convert.ToInt32(row.GetValue(0));
            var symbol = GetNullableString(row, 1)
                ?? throw new InvalidOperationException($"Operator has no Symbol (OperatorID={operatorId}).");

            yield return new Dpm20OperatorRow(operatorId, symbol);
        }
    }

    // ------------------------------------------------------------------
    // NULL-tolerant read helpers (same pattern as Dpm10AccessReader)
    // ------------------------------------------------------------------

    private static string? GetNullableString(IDataRecord record, int ordinal)
        => record.IsDBNull(ordinal) ? null : Convert.ToString(record.GetValue(ordinal));

    private static int? GetNullableInt32(IDataRecord record, int ordinal)
        => record.IsDBNull(ordinal) ? null : Convert.ToInt32(record.GetValue(ordinal));
}
