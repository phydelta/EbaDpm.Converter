using System.Data;
using System.Data.OleDb;

namespace EbaDpm.Converter.Core.Access;

/// <summary>
/// Implementation of <see cref="IDpmSourceReader"/> for the DPM 1.0 Access database
/// (<c>DPM 1.0 Database_v4_1_20250709.accdb</c> and later releases of the same format),
/// read via ACE OLEDB. Windows-only x64.
///
/// Every read uses <see cref="OleDbDataReader"/> in streaming mode: <c>DataTable</c> /
/// <c>DataAdapter</c> are not used, because some source tables exceed 2 million rows.
///
/// Note: <c>Domain</c>, <c>Order</c>, <c>Level</c>, <c>Table</c> and <c>Value</c> are reserved
/// words in Access SQL: any SQL that uses them as a table or column name must put them in
/// square brackets. For uniformity, every identifier in this file is bracketed, whether it
/// needs it or not.
/// </summary>
public sealed class Dpm10AccessReader : IDpmSourceReader
{
    private readonly string _connectionString;
    private OleDbConnection? _connection;
    private bool _disposed;

    public Dpm10AccessReader(string accdbPath)
    {
        if (string.IsNullOrWhiteSpace(accdbPath))
        {
            throw new ArgumentException("The .accdb file path cannot be empty.", nameof(accdbPath));
        }

        _connectionString =
            $"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={accdbPath};Persist Security Info=False;";
    }

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connection is not null)
        {
            return;
        }

        _connection = new OleDbConnection(_connectionString);
        _connection.Open();
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

    // ------------------------------------------------------------------
    // Generic streaming read
    // ------------------------------------------------------------------

    /// <summary>
    /// Executes <paramref name="sql"/> against the Access database and lazily returns the rows via
    /// <see cref="OleDbDataReader"/>. Basis of all the typed methods of this class.
    ///
    /// Note: the <see cref="IDataRecord"/> returned at each iteration is the same reused
    /// underlying reader: the needed values must be extracted in that same iteration, never keep
    /// the reference to read it later.
    /// </summary>
    /// <param name="sql">SQL statement, with reserved identifiers in square brackets.</param>
    /// <param name="parameters">
    /// Positional values for the <c>?</c> markers of <paramref name="sql"/>, in order.
    /// </param>
    public IEnumerable<IDataRecord> Query(string sql, IReadOnlyList<object>? parameters = null)
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
                "The Access connection is not open. Call Open() before reading.");
        }
    }

    // ------------------------------------------------------------------
    // Typed reads (IDpmSourceReader)
    // ------------------------------------------------------------------

    public IEnumerable<AccessOwnerRow> ReadOwners()
    {
        const string sql =
            """
            SELECT [OwnerID], [OwnerName], [OwnerNamespace], [OwnerLocation], [OwnerPrefix],
                   [OwnerCopyright], [ParentOwnerID], [ConceptID]
            FROM [Owner]
            """;

        foreach (var row in Query(sql))
        {
            // Ordinals read in increasing order (mandatory with SequentialAccess).
            var ownerId = Convert.ToInt32(row.GetValue(0));
            var ownerName = GetNullableString(row, 1);
            var ownerNamespace = GetNullableString(row, 2);
            var ownerLocation = GetNullableString(row, 3);
            var ownerPrefix = GetNullableString(row, 4);
            var ownerCopyright = GetNullableString(row, 5);
            var parentOwnerId = GetNullableInt32(row, 6);
            var conceptId = GetNullableInt32(row, 7);

            yield return new AccessOwnerRow(
                OwnerId: ownerId,
                OwnerName: ownerName,
                OwnerNamespace: ownerNamespace,
                OwnerLocation: ownerLocation,
                OwnerPrefix: ownerPrefix,
                OwnerCopyright: ownerCopyright,
                ParentOwnerId: parentOwnerId,
                ConceptId: conceptId);
        }
    }

    public IEnumerable<AccessReportingFrameworkRow> ReadReportingFrameworks()
    {
        const string sql =
            "SELECT [FrameworkID], [FrameworkCode], [FrameworkLabel], [ConceptID] FROM [ReportingFramework]";

        foreach (var row in Query(sql))
        {
            var frameworkId = Convert.ToInt32(row.GetValue(0));
            var frameworkCode = GetNullableString(row, 1);
            var frameworkLabel = GetNullableString(row, 2);
            var conceptId = GetNullableInt32(row, 3);

            yield return new AccessReportingFrameworkRow(
                FrameworkId: frameworkId,
                FrameworkCode: frameworkCode,
                FrameworkLabel: frameworkLabel,
                ConceptId: conceptId);
        }
    }

    public IEnumerable<AccessTaxonomyRow> ReadTaxonomies()
    {
        const string sql =
            """
            SELECT [TaxonomyID], [FrameworkID], [TaxonomyCode], [TaxonomyLabel], [TechnicalStandard],
                   [NotionalPublicationDate], [ActualPublicationDate], [DpmPackageCode], [ConceptID]
            FROM [Taxonomy]
            """;

        foreach (var row in Query(sql))
        {
            // CommandBehavior.SequentialAccess requires reading the ordinals in increasing order:
            // they must be dumped into local variables in that order before building the record,
            // not read in the order in which the named parameters appear.
            var taxonomyId = Convert.ToInt32(row.GetValue(0));
            var frameworkId = GetNullableInt32(row, 1);
            var taxonomyCode = GetNullableString(row, 2)
                ?? throw new InvalidOperationException($"Taxonomy has no TaxonomyCode (TaxonomyID={taxonomyId}).");
            var taxonomyLabel = GetNullableString(row, 3);
            var technicalStandard = GetNullableString(row, 4);
            var notionalPublicationDate = GetNullableDateTime(row, 5);
            var actualPublicationDate = GetNullableDateTime(row, 6);
            var dpmPackageCode = GetNullableString(row, 7)
                ?? throw new InvalidOperationException(
                    $"Taxonomy has no DpmPackageCode (TaxonomyID={taxonomyId}); the column is NOT NULL in the source schema.");
            var conceptId = GetNullableInt32(row, 8);

            yield return new AccessTaxonomyRow(
                TaxonomyId: taxonomyId,
                FrameworkId: frameworkId,
                TaxonomyCode: taxonomyCode,
                TaxonomyLabel: taxonomyLabel,
                TechnicalStandard: technicalStandard,
                NotionalPublicationDate: notionalPublicationDate,
                ActualPublicationDate: actualPublicationDate,
                DpmPackageCode: dpmPackageCode,
                ConceptId: conceptId);
        }
    }

    public IEnumerable<AccessConceptRow> ReadConceptsByIds(IEnumerable<int> conceptIds)
    {
        ArgumentNullException.ThrowIfNull(conceptIds);

        const int chunkSize = 500;
        var distinctIds = conceptIds.Distinct().ToList();

        for (var offset = 0; offset < distinctIds.Count; offset += chunkSize)
        {
            var chunk = distinctIds.Skip(offset).Take(chunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select(_ => "?"));
            var sql =
                $"""
                SELECT [ConceptID], [ConceptType], [OwnerID], [CreationDate], [ModificationDate], [FromDate], [ToDate]
                FROM [Concept]
                WHERE [ConceptID] IN ({placeholders})
                """;

            foreach (var row in Query(sql, chunk.Cast<object>().ToList()))
            {
                var conceptId = Convert.ToInt32(row.GetValue(0));
                var conceptType = GetNullableString(row, 1);
                var ownerId = GetNullableInt32(row, 2);
                var creationDate = GetNullableDateTime(row, 3);
                var modificationDate = GetNullableDateTime(row, 4);
                var fromDate = GetNullableDateTime(row, 5);
                var toDate = GetNullableDateTime(row, 6);

                yield return new AccessConceptRow(
                    ConceptId: conceptId,
                    ConceptType: conceptType,
                    OwnerId: ownerId,
                    CreationDate: creationDate,
                    ModificationDate: modificationDate,
                    FromDate: fromDate,
                    ToDate: toDate);
            }
        }
    }

    public int ReadMaxConceptId()
    {
        const string sql = "SELECT MAX([ConceptID]) FROM [Concept]";

        foreach (var row in Query(sql))
        {
            return Convert.ToInt32(row.GetValue(0));
        }

        throw new InvalidOperationException("The Access Concept table is empty; MAX(ConceptID) cannot be computed.");
    }

    public IEnumerable<AccessTaxonomyTableCount> ReadTaxonomyTableCounts()
    {
        const string sql =
            "SELECT [TaxonomyID], COUNT(*) AS [TableCount] FROM [TaxonomyTableVersion] GROUP BY [TaxonomyID]";

        foreach (var row in Query(sql))
        {
            yield return new AccessTaxonomyTableCount(
                TaxonomyId: Convert.ToInt32(row.GetValue(0)),
                TableCount: Convert.ToInt32(row.GetValue(1)));
        }
    }

    // ------------------------------------------------------------------
    // Dictionary
    // ------------------------------------------------------------------

    public IEnumerable<AccessDomainRow> ReadDomains()
    {
        const string sql =
            """
            SELECT [DomainID], [DomainCode], [DomainLabel], [IsTypedDomain], [IsExternalRefData],
                   [ReferenceDataSource], [DataTypeID], [DomainDescription], [DomainXbrlCode], [ConceptID]
            FROM [Domain]
            """;

        foreach (var row in Query(sql))
        {
            var domainId = Convert.ToInt32(row.GetValue(0));
            var domainCode = GetNullableString(row, 1)
                ?? throw new InvalidOperationException($"Domain has no DomainCode (DomainID={domainId}).");
            var domainLabel = GetNullableString(row, 2)
                ?? throw new InvalidOperationException($"Domain has no DomainLabel (DomainID={domainId}).");
            var isTypedDomain = Convert.ToBoolean(row.GetValue(3));
            var isExternalRefData = Convert.ToBoolean(row.GetValue(4));
            var referenceDataSource = GetNullableString(row, 5);
            var dataTypeId = GetNullableInt32(row, 6);
            var domainDescription = GetNullableString(row, 7);
            var domainXbrlCode = GetNullableString(row, 8)
                ?? throw new InvalidOperationException($"Domain has no DomainXbrlCode (DomainID={domainId}).");
            var conceptId = GetNullableInt32(row, 9);

            yield return new AccessDomainRow(
                DomainId: domainId,
                DomainCode: domainCode,
                DomainLabel: domainLabel,
                IsTypedDomain: isTypedDomain,
                IsExternalRefData: isExternalRefData,
                ReferenceDataSource: referenceDataSource,
                DataTypeId: dataTypeId,
                DomainDescription: domainDescription,
                DomainXbrlCode: domainXbrlCode,
                ConceptId: conceptId);
        }
    }

    public IEnumerable<AccessMemberRow> ReadMembers()
    {
        const string sql =
            """
            SELECT [MemberID], [DomainID], [MemberCode], [MemberLabel], [IsDefaultMember],
                   [MemberXbrlCode], [MemberDescription], [ConceptID]
            FROM [Member]
            """;

        foreach (var row in Query(sql))
        {
            var memberId = Convert.ToInt32(row.GetValue(0));
            var domainId = GetNullableInt32(row, 1);
            var memberCode = GetNullableString(row, 2)
                ?? throw new InvalidOperationException($"Member has no MemberCode (MemberID={memberId}).");
            var memberLabel = GetNullableString(row, 3)
                ?? throw new InvalidOperationException($"Member has no MemberLabel (MemberID={memberId}).");
            var isDefaultMember = Convert.ToBoolean(row.GetValue(4));
            var memberXbrlCode = GetNullableString(row, 5);
            var memberDescription = GetNullableString(row, 6);
            var conceptId = GetNullableInt32(row, 7);

            yield return new AccessMemberRow(
                MemberId: memberId,
                DomainId: domainId,
                MemberCode: memberCode,
                MemberLabel: memberLabel,
                IsDefaultMember: isDefaultMember,
                MemberXbrlCode: memberXbrlCode,
                MemberDescription: memberDescription,
                ConceptId: conceptId);
        }
    }

    public IEnumerable<AccessDimensionRow> ReadDimensions()
    {
        const string sql =
            """
            SELECT [DimensionID], [DomainID], [DimensionCode], [DimensionLabel], [DimensionDescription],
                   [IsImpliedIfNotExplicitlyModelled], [DimensionXbrlCode], [ConceptID]
            FROM [Dimension]
            """;

        foreach (var row in Query(sql))
        {
            var dimensionId = Convert.ToInt32(row.GetValue(0));
            var domainId = Convert.ToInt32(row.GetValue(1));
            var dimensionCode = GetNullableString(row, 2)
                ?? throw new InvalidOperationException($"Dimension has no DimensionCode (DimensionID={dimensionId}).");
            var dimensionLabel = GetNullableString(row, 3)
                ?? throw new InvalidOperationException($"Dimension has no DimensionLabel (DimensionID={dimensionId}).");
            var dimensionDescription = GetNullableString(row, 4);
            var isImplied = Convert.ToBoolean(row.GetValue(5));
            var dimensionXbrlCode = GetNullableString(row, 6);
            var conceptId = GetNullableInt32(row, 7);

            yield return new AccessDimensionRow(
                DimensionId: dimensionId,
                DomainId: domainId,
                DimensionCode: dimensionCode,
                DimensionLabel: dimensionLabel,
                DimensionDescription: dimensionDescription,
                IsImpliedIfNotExplicitlyModelled: isImplied,
                DimensionXbrlCode: dimensionXbrlCode,
                ConceptId: conceptId);
        }
    }

    public IEnumerable<AccessMetricRow> ReadMetrics()
    {
        const string sql =
            """
            SELECT [MetricID], [DataTypeID], [FlowTypeID], [CodeDomainID], [CodeSubdomainID],
                   [StringListID], [RequiredDataSign], [TypicalDataSign], [Additivity]
            FROM [Metric]
            """;

        foreach (var row in Query(sql))
        {
            var metricId = Convert.ToInt32(row.GetValue(0));
            var dataTypeId = Convert.ToInt32(row.GetValue(1));
            var flowTypeId = Convert.ToInt32(row.GetValue(2));
            var codeDomainId = GetNullableInt32(row, 3);
            var codeSubdomainId = GetNullableInt32(row, 4);
            var stringListId = GetNullableInt32(row, 5);
            var requiredDataSign = GetNullableString(row, 6);
            var typicalDataSign = GetNullableString(row, 7);
            var additivity = GetNullableString(row, 8);

            yield return new AccessMetricRow(
                MetricId: metricId,
                DataTypeId: dataTypeId,
                FlowTypeId: flowTypeId,
                CodeDomainId: codeDomainId,
                CodeSubdomainId: codeSubdomainId,
                StringListId: stringListId,
                RequiredDataSign: requiredDataSign,
                TypicalDataSign: typicalDataSign,
                Additivity: additivity);
        }
    }

    public IEnumerable<AccessHierarchyRow> ReadHierarchies()
    {
        // Deterministic ORDER BY: a few Hierarchy rows share their ConceptID with another, different
        // Hierarchy row (a data defect in the Access database — Domain, Member and Dimension do
        // not have it). DictionaryLoader.LoadConceptTranslations keeps the first translation seen
        // per ConceptID; this ordering makes that choice reproducible.
        const string sql =
            "SELECT [HierarchyID], [HierarchyCode], [HierarchyLabel], [DomainID], [HierarchyDescription], [ConceptID] FROM [Hierarchy] ORDER BY [HierarchyID]";

        foreach (var row in Query(sql))
        {
            var hierarchyId = Convert.ToInt32(row.GetValue(0));
            var hierarchyCode = GetNullableString(row, 1)
                ?? throw new InvalidOperationException($"Hierarchy has no HierarchyCode (HierarchyID={hierarchyId}).");
            var hierarchyLabel = GetNullableString(row, 2);
            var domainId = GetNullableInt32(row, 3);
            var hierarchyDescription = GetNullableString(row, 4);
            var conceptId = GetNullableInt32(row, 5);

            yield return new AccessHierarchyRow(
                HierarchyId: hierarchyId,
                HierarchyCode: hierarchyCode,
                HierarchyLabel: hierarchyLabel,
                DomainId: domainId,
                HierarchyDescription: hierarchyDescription,
                ConceptId: conceptId);
        }
    }

    public IEnumerable<AccessHierarchyNodeRow> ReadHierarchyNodes()
    {
        // Order and Level are reserved words in Access SQL: they go in square brackets.
        // ORDER BY [HierarchyID], [MemberID] allows assigning the synthetic HierarchyNodeID while
        // streaming (DictionaryLoader), without materializing the whole table.
        const string sql =
            """
            SELECT [HierarchyID], [MemberID], [IsAbstract], [ComparisonOperator], [UnaryOperator],
                   [Order], [Level], [Path], [ParentMemberID], [ConceptID]
            FROM [HierarchyNode]
            ORDER BY [HierarchyID], [MemberID]
            """;

        foreach (var row in Query(sql))
        {
            var hierarchyId = Convert.ToInt32(row.GetValue(0));
            var memberId = Convert.ToInt32(row.GetValue(1));
            var isAbstract = Convert.ToBoolean(row.GetValue(2));
            var comparisonOperator = GetNullableString(row, 3);
            var unaryOperator = GetNullableString(row, 4);
            var order = Convert.ToInt32(row.GetValue(5));
            var level = Convert.ToInt32(row.GetValue(6));
            var path = GetNullableString(row, 7);
            var parentMemberId = GetNullableInt32(row, 8);
            var conceptId = GetNullableInt32(row, 9);

            yield return new AccessHierarchyNodeRow(
                HierarchyId: hierarchyId,
                MemberId: memberId,
                IsAbstract: isAbstract,
                ComparisonOperator: comparisonOperator,
                UnaryOperator: unaryOperator,
                Order: order,
                Level: level,
                Path: path,
                ParentMemberId: parentMemberId,
                ConceptId: conceptId);
        }
    }

    public IEnumerable<AccessDataTypeRow> ReadDataTypes()
    {
        const string sql = "SELECT [DataTypeID], [DataTypeCode], [DataTypeLabel] FROM [DataType]";

        foreach (var row in Query(sql))
        {
            var dataTypeId = Convert.ToInt32(row.GetValue(0));
            var dataTypeCode = GetNullableString(row, 1);
            // DataTypeLabel is a fixed WChar(50): in practice Access does not pad it with spaces,
            // but it is trimmed anyway for safety (see FlowTypeLabel).
            var dataTypeLabel = GetNullableString(row, 2)?.Trim()
                ?? throw new InvalidOperationException($"DataType has no DataTypeLabel (DataTypeID={dataTypeId}).");

            yield return new AccessDataTypeRow(dataTypeId, dataTypeCode, dataTypeLabel);
        }
    }

    public IEnumerable<AccessFlowTypeRow> ReadFlowTypes()
    {
        const string sql = "SELECT [FlowTypeID], [FlowTypeCode], [FlowTypeLabel] FROM [FlowType]";

        foreach (var row in Query(sql))
        {
            var flowTypeId = Convert.ToInt32(row.GetValue(0));
            var flowTypeCode = GetNullableString(row, 1);
            // FlowTypeLabel is a FIXED WChar(50) and Access pads it with spaces up to 50
            // characters ("Stock" is really 50 chars with padding): it must be trimmed, or
            // "Stock" with trailing spaces would contaminate mMetric.FlowType.
            var flowTypeLabel = GetNullableString(row, 2)?.Trim()
                ?? throw new InvalidOperationException($"FlowType has no FlowTypeLabel (FlowTypeID={flowTypeId}).");

            yield return new AccessFlowTypeRow(flowTypeId, flowTypeCode, flowTypeLabel);
        }
    }

    // ------------------------------------------------------------------
    // Templates and tables
    // ------------------------------------------------------------------

    public IEnumerable<AccessTableGroupRow> ReadTableGroups()
    {
        const string sql =
            "SELECT [TableGroupID], [TaxonomyID], [TableGroupCode], [TableGroupLabel], [Order], [ConceptID] FROM [TableGroup]";

        foreach (var row in Query(sql))
        {
            var tableGroupId = Convert.ToInt32(row.GetValue(0));
            var taxonomyId = GetNullableInt32(row, 1);
            var tableGroupCode = GetNullableString(row, 2)
                ?? throw new InvalidOperationException($"TableGroup has no TableGroupCode (TableGroupID={tableGroupId}).");
            var tableGroupLabel = GetNullableString(row, 3);
            var order = GetNullableInt32(row, 4);
            var conceptId = GetNullableInt32(row, 5);

            yield return new AccessTableGroupRow(
                TableGroupId: tableGroupId,
                TaxonomyId: taxonomyId,
                TableGroupCode: tableGroupCode,
                TableGroupLabel: tableGroupLabel,
                Order: order,
                ConceptId: conceptId);
        }
    }

    public IEnumerable<AccessTemplateRow> ReadTemplates()
    {
        const string sql = "SELECT [TemplateID], [TemplateCode], [TemplateLabel], [ConceptID] FROM [Template]";

        foreach (var row in Query(sql))
        {
            var templateId = Convert.ToInt32(row.GetValue(0));
            var templateCode = GetNullableString(row, 1)
                ?? throw new InvalidOperationException($"Template has no TemplateCode (TemplateID={templateId}).");
            var templateLabel = GetNullableString(row, 2)
                ?? throw new InvalidOperationException($"Template has no TemplateLabel (TemplateID={templateId}).");
            var conceptId = GetNullableInt32(row, 3);

            yield return new AccessTemplateRow(
                TemplateId: templateId,
                TemplateCode: templateCode,
                TemplateLabel: templateLabel,
                ConceptId: conceptId);
        }
    }

    public IEnumerable<AccessTaxonomyTableVersionRow> ReadTaxonomyTableVersions()
    {
        const string sql =
            "SELECT [TaxonomyID], [TableVID], [TemplateID], [TableGroupID], [IsSimpleReuse] FROM [TaxonomyTableVersion]";

        foreach (var row in Query(sql))
        {
            yield return new AccessTaxonomyTableVersionRow(
                TaxonomyId: Convert.ToInt32(row.GetValue(0)),
                TableVId: Convert.ToInt32(row.GetValue(1)),
                TemplateId: GetNullableInt32(row, 2),
                TableGroupId: GetNullableInt32(row, 3),
                IsSimpleReuse: Convert.ToBoolean(row.GetValue(4)));
        }
    }

    public IEnumerable<AccessTableVersionRow> ReadTableVersionsByIds(IEnumerable<int> tableVIds)
    {
        ArgumentNullException.ThrowIfNull(tableVIds);

        const int chunkSize = 500;
        var distinctIds = tableVIds.Distinct().ToList();

        for (var offset = 0; offset < distinctIds.Count; offset += chunkSize)
        {
            var chunk = distinctIds.Skip(offset).Take(chunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select(_ => "?"));
            var sql =
                $"""
                SELECT [TableVID], [TableID], [TableVersionCode], [TableVersionLabel],
                       [XbrlFilingIndicatorCode], [XbrlTableCode], [FromDate], [ToDate], [ConceptID]
                FROM [TableVersion]
                WHERE [TableVID] IN ({placeholders})
                """;

            foreach (var row in Query(sql, chunk.Cast<object>().ToList()))
            {
                yield return new AccessTableVersionRow(
                    TableVId: Convert.ToInt32(row.GetValue(0)),
                    TableId: GetNullableInt32(row, 1),
                    TableVersionCode: GetNullableString(row, 2),
                    TableVersionLabel: GetNullableString(row, 3),
                    XbrlFilingIndicatorCode: GetNullableString(row, 4),
                    XbrlTableCode: GetNullableString(row, 5),
                    FromDate: GetNullableDateTime(row, 6),
                    ToDate: GetNullableDateTime(row, 7),
                    ConceptId: GetNullableInt32(row, 8));
            }
        }
    }

    public IEnumerable<AccessTableRow> ReadTablesByIds(IEnumerable<int> tableIds)
    {
        ArgumentNullException.ThrowIfNull(tableIds);

        const int chunkSize = 500;
        var distinctIds = tableIds.Distinct().ToList();

        for (var offset = 0; offset < distinctIds.Count; offset += chunkSize)
        {
            var chunk = distinctIds.Skip(offset).Take(chunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select(_ => "?"));
            // [Table] is a reserved word in Access SQL (see the header comment of this file).
            var sql =
                $"""
                SELECT [TableID], [TemplateID], [OriginalTableCode], [OriginalTableLabel], [ConceptID]
                FROM [Table]
                WHERE [TableID] IN ({placeholders})
                """;

            foreach (var row in Query(sql, chunk.Cast<object>().ToList()))
            {
                var tableId = Convert.ToInt32(row.GetValue(0));
                var templateId = GetNullableInt32(row, 1);
                var originalTableCode = GetNullableString(row, 2)
                    ?? throw new InvalidOperationException($"Table has no OriginalTableCode (TableID={tableId}).");
                var originalTableLabel = GetNullableString(row, 3)
                    ?? throw new InvalidOperationException($"Table has no OriginalTableLabel (TableID={tableId}).");
                var conceptId = GetNullableInt32(row, 4);

                yield return new AccessTableRow(
                    TableId: tableId,
                    TemplateId: templateId,
                    OriginalTableCode: originalTableCode,
                    OriginalTableLabel: originalTableLabel,
                    ConceptId: conceptId);
            }
        }
    }

    // ------------------------------------------------------------------
    // Modules
    // ------------------------------------------------------------------

    public IEnumerable<AccessModuleRow> ReadModules()
    {
        // Module and Version are reserved words in Access SQL: in square brackets. Not read:
        // ConceptualModuleID (mConceptualModule does not come from Access.ConceptualModule) nor
        // Version/FromDate/ToDate/isDocumentModule (no target).
        const string sql =
            "SELECT [ModuleID], [TaxonomyID], [ModuleCode], [ModuleLabel], [XbrlSchemaRef], [ConceptID] FROM [Module]";

        foreach (var row in Query(sql))
        {
            var moduleId = Convert.ToInt32(row.GetValue(0));
            var taxonomyId = GetNullableInt32(row, 1)
                ?? throw new InvalidOperationException($"Module has no TaxonomyID (ModuleID={moduleId}).");
            var moduleCode = GetNullableString(row, 2)
                ?? throw new InvalidOperationException($"Module has no ModuleCode (ModuleID={moduleId}).");
            var moduleLabel = GetNullableString(row, 3)
                ?? throw new InvalidOperationException($"Module has no ModuleLabel (ModuleID={moduleId}).");
            var xbrlSchemaRef = GetNullableString(row, 4);
            var conceptId = GetNullableInt32(row, 5);

            yield return new AccessModuleRow(
                ModuleId: moduleId,
                TaxonomyId: taxonomyId,
                ModuleCode: moduleCode,
                ModuleLabel: moduleLabel,
                XbrlSchemaRef: xbrlSchemaRef,
                ConceptId: conceptId);
        }
    }

    public IEnumerable<AccessModuleTableVersionRow> ReadModuleTableVersions()
    {
        const string sql = "SELECT [ModuleID], [TableVID] FROM [ModuleTableVersion]";

        foreach (var row in Query(sql))
        {
            yield return new AccessModuleTableVersionRow(
                ModuleId: Convert.ToInt32(row.GetValue(0)),
                TableVId: Convert.ToInt32(row.GetValue(1)));
        }
    }

    // ------------------------------------------------------------------
    // Cells and open-axis restrictions
    // ------------------------------------------------------------------

    public IEnumerable<AccessAxisRow> ReadAxesByTableVIds(IEnumerable<int> tableVIds)
    {
        ArgumentNullException.ThrowIfNull(tableVIds);

        const int chunkSize = 500;
        var distinctIds = tableVIds.Distinct().ToList();

        for (var offset = 0; offset < distinctIds.Count; offset += chunkSize)
        {
            var chunk = distinctIds.Skip(offset).Take(chunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select(_ => "?"));
            var sql =
                $"""
                SELECT [AxisID], [TableVID], [AxisOrientation], [AxisLabel], [AxisOrder], [IsOpenAxis], [ConceptID]
                FROM [Axis]
                WHERE [TableVID] IN ({placeholders})
                """;

            foreach (var row in Query(sql, chunk.Cast<object>().ToList()))
            {
                yield return new AccessAxisRow(
                    AxisId: Convert.ToInt32(row.GetValue(0)),
                    TableVId: GetNullableInt32(row, 1),
                    AxisOrientation: GetNullableString(row, 2),
                    AxisLabel: GetNullableString(row, 3),
                    AxisOrder: GetNullableInt32(row, 4),
                    IsOpenAxis: Convert.ToBoolean(row.GetValue(5)),
                    ConceptId: GetNullableInt32(row, 6));
            }
        }
    }

    public IEnumerable<AccessAxisOrdinateRow> ReadAxisOrdinatesByTableVIds(IEnumerable<int> tableVIds)
    {
        ArgumentNullException.ThrowIfNull(tableVIds);

        const int chunkSize = 500;
        var distinctIds = tableVIds.Distinct().ToList();

        for (var offset = 0; offset < distinctIds.Count; offset += chunkSize)
        {
            var chunk = distinctIds.Skip(offset).Take(chunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select(_ => "?"));
            // Order and Level are reserved words in Access SQL: in square brackets.
            var sql =
                $"""
                SELECT ao.[OrdinateID], ao.[AxisID], ao.[IsAbstractHeader], ao.[OrdinateCode], ao.[OrdinateLabel],
                       ao.[Order], ao.[Level], ao.[Path], ao.[ParentOrdinateID], ao.[DisplayBeforeChildren],
                       ao.[CategorisationKey], ao.[IsRowKey], ao.[RequiredDataSign], ao.[TypicalDataSign], ao.[ConceptID]
                FROM [AxisOrdinate] ao INNER JOIN [Axis] a ON a.[AxisID] = ao.[AxisID]
                WHERE a.[TableVID] IN ({placeholders})
                """;

            foreach (var row in Query(sql, chunk.Cast<object>().ToList()))
            {
                var ordinateId = Convert.ToInt32(row.GetValue(0));
                var axisId = Convert.ToInt32(row.GetValue(1));
                var isAbstractHeader = Convert.ToBoolean(row.GetValue(2));
                // OrdinateCode comes padded to 4 characters; it is trimmed here.
                var ordinateCode = GetNullableString(row, 3)?.Trim()
                    ?? throw new InvalidOperationException($"AxisOrdinate has no OrdinateCode (OrdinateID={ordinateId}).");
                var ordinateLabel = GetNullableString(row, 4)
                    ?? throw new InvalidOperationException($"AxisOrdinate has no OrdinateLabel (OrdinateID={ordinateId}).");
                var order = Convert.ToInt32(row.GetValue(5));
                var level = Convert.ToInt32(row.GetValue(6));
                var path = GetNullableString(row, 7);
                var parentOrdinateId = GetNullableInt32(row, 8);
                var displayBeforeChildren = Convert.ToBoolean(row.GetValue(9));
                var categorisationKey = GetNullableString(row, 10);
                var isRowKey = Convert.ToBoolean(row.GetValue(11));
                var requiredDataSign = GetNullableString(row, 12);
                var typicalDataSign = GetNullableString(row, 13);
                var conceptId = GetNullableInt32(row, 14);

                yield return new AccessAxisOrdinateRow(
                    OrdinateId: ordinateId,
                    AxisId: axisId,
                    IsAbstractHeader: isAbstractHeader,
                    OrdinateCode: ordinateCode,
                    OrdinateLabel: ordinateLabel,
                    Order: order,
                    Level: level,
                    Path: path,
                    ParentOrdinateId: parentOrdinateId,
                    DisplayBeforeChildren: displayBeforeChildren,
                    CategorisationKey: categorisationKey,
                    IsRowKey: isRowKey,
                    RequiredDataSign: requiredDataSign,
                    TypicalDataSign: typicalDataSign,
                    ConceptId: conceptId);
            }
        }
    }

    public IEnumerable<AccessOrdinateCategorisationRow> ReadOrdinateCategorisationsByTableVIds(IEnumerable<int> tableVIds)
    {
        ArgumentNullException.ThrowIfNull(tableVIds);

        const int chunkSize = 500;
        var distinctIds = tableVIds.Distinct().ToList();

        for (var offset = 0; offset < distinctIds.Count; offset += chunkSize)
        {
            var chunk = distinctIds.Skip(offset).Take(chunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select(_ => "?"));
            var sql =
                $"""
                SELECT oc.[OrdinateID], oc.[DimensionID], oc.[MemberID], oc.[RestrictionID]
                FROM ([OrdinateCategorisation] oc
                      INNER JOIN [AxisOrdinate] ao ON ao.[OrdinateID] = oc.[OrdinateID])
                      INNER JOIN [Axis] a ON a.[AxisID] = ao.[AxisID]
                WHERE a.[TableVID] IN ({placeholders})
                """;

            foreach (var row in Query(sql, chunk.Cast<object>().ToList()))
            {
                yield return new AccessOrdinateCategorisationRow(
                    OrdinateId: Convert.ToInt32(row.GetValue(0)),
                    DimensionId: Convert.ToInt32(row.GetValue(1)),
                    MemberId: Convert.ToInt32(row.GetValue(2)),
                    RestrictionId: GetNullableInt32(row, 3));
            }
        }
    }

    public IEnumerable<AccessTableCellRow> ReadTableCellsByTableVIds(IEnumerable<int> tableVIds)
    {
        ArgumentNullException.ThrowIfNull(tableVIds);

        const int chunkSize = 500;
        var distinctIds = tableVIds.Distinct().ToList();

        for (var offset = 0; offset < distinctIds.Count; offset += chunkSize)
        {
            var chunk = distinctIds.Skip(offset).Take(chunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select(_ => "?"));
            // DataPointVID is NOT read (DataPoint is discarded from the target).
            var sql =
                $"""
                SELECT [CellID], [TableVID], [IsShaded], [CellCode]
                FROM [TableCell]
                WHERE [TableVID] IN ({placeholders})
                """;

            foreach (var row in Query(sql, chunk.Cast<object>().ToList()))
            {
                var cellId = Convert.ToInt32(row.GetValue(0));
                var tableVId = GetNullableInt32(row, 1);
                var isShaded = Convert.ToBoolean(row.GetValue(2));
                var cellCode = GetNullableString(row, 3)
                    ?? throw new InvalidOperationException($"TableCell has no CellCode (CellID={cellId}).");

                yield return new AccessTableCellRow(
                    CellId: cellId,
                    TableVId: tableVId,
                    IsShaded: isShaded,
                    CellCode: cellCode);
            }
        }
    }

    public IEnumerable<AccessCellPositionRow> ReadCellPositionsByTableVIds(IEnumerable<int> tableVIds)
    {
        ArgumentNullException.ThrowIfNull(tableVIds);

        // Hot spot of the project (up to about 2.4 million rows): strictly streaming, without
        // materializing any intermediate list of positions. Chunking tableVIds only materializes
        // the (small) list of IDs itself, never the CellPosition rows.
        const int chunkSize = 500;
        var distinctIds = tableVIds.Distinct().ToList();

        for (var offset = 0; offset < distinctIds.Count; offset += chunkSize)
        {
            var chunk = distinctIds.Skip(offset).Take(chunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select(_ => "?"));
            var sql =
                $"""
                SELECT cp.[CellID], cp.[OrdinateID]
                FROM [CellPosition] cp INNER JOIN [TableCell] tc ON tc.[CellID] = cp.[CellID]
                WHERE tc.[TableVID] IN ({placeholders})
                ORDER BY cp.[CellID]
                """;

            foreach (var row in Query(sql, chunk.Cast<object>().ToList()))
            {
                yield return new AccessCellPositionRow(
                    CellId: Convert.ToInt32(row.GetValue(0)),
                    OrdinateId: Convert.ToInt32(row.GetValue(1)));
            }
        }
    }

    public IEnumerable<AccessOpenMemberRestrictionRow> ReadOpenMemberRestrictions()
    {
        const string sql =
            """
            SELECT [RestrictionID], [HierarchyID], [MemberID], [MemberIncluded], [AllowsDefaultMember], [IgnoreMemberID]
            FROM [OpenMemberRestriction]
            """;

        foreach (var row in Query(sql))
        {
            yield return new AccessOpenMemberRestrictionRow(
                RestrictionId: Convert.ToInt32(row.GetValue(0)),
                HierarchyId: GetNullableInt32(row, 1),
                MemberId: GetNullableInt32(row, 2),
                MemberIncluded: Convert.ToBoolean(row.GetValue(3)),
                AllowsDefaultMember: Convert.ToBoolean(row.GetValue(4)),
                IgnoreMemberId: Convert.ToBoolean(row.GetValue(5)));
        }
    }

    public IEnumerable<AccessOpenAxisValueRestrictionRow> ReadOpenAxisValueRestrictions()
    {
        // NOT the source of mOpenAxisValueRestriction (see the XML docs of the interface and of the
        // record). It is read in full because it is small and its only consumer is a coherence
        // assertion in the tests.
        const string sql = "SELECT [AxisID], [RestrictionID] FROM [OpenAxisValueRestriction]";

        foreach (var row in Query(sql))
        {
            yield return new AccessOpenAxisValueRestrictionRow(
                AxisId: Convert.ToInt32(row.GetValue(0)),
                RestrictionId: Convert.ToInt32(row.GetValue(1)));
        }
    }

    // ------------------------------------------------------------------
    // NULL-tolerant read helpers
    // ------------------------------------------------------------------

    private static string? GetNullableString(IDataRecord record, int ordinal)
        => record.IsDBNull(ordinal) ? null : Convert.ToString(record.GetValue(ordinal));

    private static int? GetNullableInt32(IDataRecord record, int ordinal)
        => record.IsDBNull(ordinal) ? null : Convert.ToInt32(record.GetValue(ordinal));

    private static DateTime? GetNullableDateTime(IDataRecord record, int ordinal)
        => record.IsDBNull(ordinal) ? null : Convert.ToDateTime(record.GetValue(ordinal));
}
