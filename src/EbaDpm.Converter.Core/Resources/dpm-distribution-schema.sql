-- DPM distribution schema (EBA 4.2 layout): 45 tables.
-- This is the output contract of the converter: the generated database must
-- reproduce this structure (tables, columns, types, PK/FK, indexes).
--
-- Note: SchemaCreator strips '--' line comments and splits the file on ';', so this
-- file must contain only line comments and CREATE TABLE statements.

-- ============================================================
-- TABLES
-- ============================================================

-- aContainerInfo
CREATE TABLE aContainerInfo
(
    ContainerName    STRING,
    ContainerVersion INTEGER
);

-- aDDSInfo
CREATE TABLE aDDSInfo
(
    ColumnsPerTable INTEGER
);

-- aDatabaseProperties
CREATE TABLE aDatabaseProperties
(
    Property TEXT primary key, [Value]
    TEXT
);

-- dFilingIndicator
CREATE TABLE dFilingIndicator
(
    InstanceID INTEGER NOT NULL,
    TableID    INTEGER NOT NULL
        REFERENCES mTable (TableID),
    Filed      BOOLEAN,
    PRIMARY KEY (
                 InstanceID,
                 TableID
        ),
    FOREIGN KEY (
                 InstanceID
        )
        REFERENCES dInstance (InstanceID) ON DELETE CASCADE
);

-- dInstance
CREATE TABLE dInstance
(
    InstanceID             INTEGER NOT NULL,
    ModuleID               INTEGER NOT NULL,
    FileName               TEXT,
    CompressedFileBlob     BLOB,
    Timestamp              DATETIME,
    EntityScheme           TEXT,
    EntityIdentifier       TEXT,
    EntityName             TEXT,
    PeriodEndDateOrInstant DATE,
    EntityCurrency         TEXT,
    PRIMARY KEY (
                 InstanceID
        )
);

-- mAxis
CREATE TABLE mAxis
(
    AxisID          INTEGER,
    AxisOrientation TEXT,
    AxisLabel       TEXT,
    IsOpenAxis      BOOLEAN,
    OptionalKey     BOOLEAN,
    ConceptID       INTEGER,
    AxisCode        TEXT,
    PRIMARY KEY (
                 AxisID
        )
);

-- mAxisOrdinate
CREATE TABLE mAxisOrdinate
(
    AxisID                  INTEGER,
    OrdinateID              INTEGER NOT NULL,
    OrdinateLabel           TEXT,
    OrdinateCode            TEXT,
    IsDisplayBeforeChildren BOOLEAN,
    IsAbstractHeader        BOOLEAN,
    IsRowKey                BOOLEAN,
    Level                   INTEGER,
    [Order]
    INTEGER,
    ParentOrdinateID        INTEGER,
    ConceptID               INTEGER,
    TypeOfKey               TEXT,
    RelatedDimensionTableId INTEGER,
    PRIMARY KEY (
                 OrdinateID
        ),
    FOREIGN KEY (
                 AxisID
        )
        REFERENCES mAxis (AxisID),
    FOREIGN KEY (
                 ParentOrdinateID
        )
        REFERENCES mAxisOrdinate (OrdinateID),
    FOREIGN KEY (
                 ConceptID
        )
        REFERENCES mConcept (ConceptID),
    FOREIGN KEY (
                 RelatedDimensionTableId
        )
        REFERENCES mTable (TableID)
);

-- mCellPosition
CREATE TABLE mCellPosition
(
    CellID     INTEGER,
    OrdinateID INTEGER,
    PRIMARY KEY (
                 CellID,
                 OrdinateID
        ),
    FOREIGN KEY (
                 OrdinateID
        )
        REFERENCES mAxisOrdinate (OrdinateID),
    FOREIGN KEY (
                 CellID
        )
        REFERENCES mTableCell (CellID)
);

-- mConcept
CREATE TABLE mConcept
(
    ConceptID        INTEGER,
    ConceptType      TEXT,
    OwnerID          INTEGER,
    ReleaseID        INTEGER,
    CreationDate     DATE,
    ModificationDate DATE,
    FromDate         DATE,
    ToDate           DATE,
    PRIMARY KEY (
                 ConceptID
        ),
    FOREIGN KEY (
                 OwnerID
        )
        REFERENCES mOwner (OwnerID)
);

-- mConceptReference
CREATE TABLE mConceptReference
(
    ConceptID   INTEGER REFERENCES mConcept (ConceptID),
    ReferenceID INTEGER REFERENCES mReference (ReferenceID)
        NOT NULL
);

-- mConceptTranslation
CREATE TABLE mConceptTranslation
(
    ConceptID  INTEGER,
    LanguageID INTEGER,
    Text       TEXT,
    Role       TEXT,
    PRIMARY KEY (
                 ConceptID,
                 LanguageID,
                 Role
        ),
    FOREIGN KEY (
                 ConceptID
        )
        REFERENCES mConcept (ConceptID),
    FOREIGN KEY (
                 LanguageID
        )
        REFERENCES mLanguage (LanguageID)
);

-- mConceptualModule
CREATE TABLE mConceptualModule
(
    ConceptualModuleID    INTEGER,
    ConceptualModuleCode  TEXT,
    ConceptualModuleLabel TEXT,
    PRIMARY KEY (
                 ConceptualModuleID
        )
);

-- mCustomDataType
CREATE TABLE mCustomDataType
(
    DataTypeID   INTEGER PRIMARY KEY,
    DataTypeJSON VARCHAR(2500) NOT NULL
);

-- mDimension
CREATE TABLE mDimension
(
    DimensionID          INTEGER,
    DimensionLabel       TEXT,
    DimensionCode        TEXT,
    DimensionDescription TEXT,
    DimensionXBRLCode    TEXT,
    DomainID             INTEGER,
    IsTypedDimension     BOOLEAN,
    ConceptID            INTEGER,
    DefaultMemberID      INTEGER,
    PRIMARY KEY (
                 DimensionID
        ),
    FOREIGN KEY (
                 ConceptID
        )
        REFERENCES mConcept (ConceptID),
    FOREIGN KEY (
                 DomainID
        )
        REFERENCES mDomain (DomainID),
    FOREIGN KEY (
                 DefaultMemberID
        )
        REFERENCES mMember (MemberID)
);

-- mDomain
CREATE TABLE mDomain
(
    DomainID          INTEGER NOT NULL,
    DomainCode        TEXT,
    DomainLabel       TEXT,
    DomainDescription TEXT,
    DomainXBRLCode    TEXT,
    DataType          TEXT,
    IsTypedDomain     BOOLEAN,
    IsNillable        BOOLEAN,
    ConceptID         INTEGER,
    PRIMARY KEY (
                 DomainID
        ),
    FOREIGN KEY (
                 ConceptID
        )
        REFERENCES mConcept (ConceptID)
);

-- mDomainUnion
CREATE TABLE mDomainUnion
(
    UnionDomainID INTEGER,
    UnitedDomainID INTEGER,
    FOREIGN KEY (
                 UnionDomainID
        )
        REFERENCES mDomain (DomainID),
    FOREIGN KEY (
                 UnitedDomainID
        )
        REFERENCES mDomain (DomainID)
);

-- mHierarchy
CREATE TABLE mHierarchy
(
    HierarchyID          INTEGER,
    HierarchyCode        TEXT,
    HierarchyLabel       TEXT,
    DomainID             INTEGER,
    HierarchyDescription TEXT,
    ConceptID            INTEGER,
    PRIMARY KEY (
                 HierarchyID
        ),
    FOREIGN KEY (
                 ConceptID
        )
        REFERENCES mConcept (ConceptID),
    FOREIGN KEY (
                 DomainID
        )
        REFERENCES mDomain (DomainID)
);

-- mHierarchyNode
CREATE TABLE mHierarchyNode
(
    HierarchyNodeID    INTEGER NOT NULL,
    HierarchyID        INTEGER,
    MemberID           INTEGER,
    IsAbstract         BOOLEAN,
    ComparisonOperator TEXT,
    UnaryOperator      TEXT, [Order]
    INTEGER,
    Level              INTEGER,
    ParentMemberID     INTEGER,
    HierarchyNodeLabel TEXT,
    ConceptID          INTEGER REFERENCES mConcept (ConceptID),
    Path               VARCHAR(3999),-- PRIMARY KEY ([HierarchyID], [MemberID]),
    PRIMARY KEY (
                 HierarchyNodeID
        ),
    FOREIGN KEY (
                 HierarchyID
        )
        REFERENCES mHierarchy (HierarchyID),
    FOREIGN KEY (
                 MemberID
        )
        REFERENCES mMember (MemberID)
);

-- mLanguage
CREATE TABLE mLanguage
(
    LanguageID   INTEGER,
    LanguageName TEXT,
    EnglishName  TEXT,
    IsoCode      TEXT,
    ConceptID    INTEGER,
    PRIMARY KEY (
                 LanguageID
        ),
    FOREIGN KEY (
                 ConceptID
        )
        REFERENCES mConcept (ConceptID)
);

-- mMember
CREATE TABLE mMember
(
    MemberID        INTEGER,
    DomainID        INTEGER,
    MemberCode      TEXT,
    MemberLabel     TEXT,
    MemberXBRLCode  TEXT,
    IsDefaultMember BOOLEAN,
    ConceptID       INTEGER,
    PRIMARY KEY (
                 MemberID
        ),
    FOREIGN KEY (
                 ConceptID
        )
        REFERENCES mConcept (ConceptID),
    FOREIGN KEY (
                 DomainID
        )
        REFERENCES mDomain (DomainID)
);

-- mMetric
CREATE TABLE mMetric
(
    MetricID                  INTEGER,
    CorrespondingMemberID     INTEGER,
    DataType                  TEXT,
    FlowType                  TEXT,
    BalanceType               TEXT,
    ReferencedDomainID        INTEGER,
    ReferencedHierarchyID     INTEGER,
    HierarchyStartingMemberID INTEGER,
    IsStartingMemberIncluded  BOOLEAN,
    IsAbstract                BOOLEAN,
    CustomDataTypeID          INTEGER REFERENCES mCustomDataType (DataTypeID),
    PRIMARY KEY (
                 MetricID
        ),
    FOREIGN KEY (
                 ReferencedDomainID
        )
        REFERENCES mDomain (DomainID),
    FOREIGN KEY (
                 ReferencedHierarchyID
        )
        REFERENCES mHierarchy (HierarchyID),
    FOREIGN KEY (
                 HierarchyStartingMemberID
        )
        REFERENCES mMember (MemberID),
    FOREIGN KEY (
                 CorrespondingMemberID
        )
        REFERENCES mMember (MemberID)
);

-- mModule
CREATE TABLE mModule
(
    ModuleID           INTEGER NOT NULL,
    TaxonomyID         INTEGER,
    ModuleCode         TEXT,
    ModuleLabel        TEXT,
    ConceptualModuleID INTEGER,
    DefaultFrequency   TEXT,
    ConceptID          INTEGER,
    XBRLSchemaRef      TEXT,
    JSONSchemaRef      TEXT,
    AutogenerateRefs   BOOLEAN,
    JsonBlob           BLOB,
    PRIMARY KEY (
                 ModuleID
        ),
    FOREIGN KEY (
                 TaxonomyID
        )
        REFERENCES mTaxonomy (TaxonomyID) ON UPDATE CASCADE,
    FOREIGN KEY (
                 ConceptualModuleID
        )
        REFERENCES mConceptualModule (ConceptualModuleID)
);

-- mModuleBusinessTemplate
CREATE TABLE mModuleBusinessTemplate
(
    ModuleID           INTEGER, [Order]
    INTEGER,
    BusinessTemplateID INTEGER,
    PRIMARY KEY (
                 ModuleID,
        [Order]
        ),
    FOREIGN KEY (
                 ModuleID
        )
        REFERENCES mModule (ModuleID)
);

-- mNamespacePrefix
CREATE TABLE mNamespacePrefix
(
    Prefix    STRING PRIMARY KEY
        UNIQUE
                            NOT NULL,
    Namespace VARCHAR(1000) NOT NULL
        UNIQUE
);

-- mOpenAxisValueRestriction
CREATE TABLE mOpenAxisValueRestriction
(
    AxisID                    INTEGER,
    HierarchyID               INTEGER,
    HierarchyStartingMemberID INTEGER,
    IsStartingMemberIncluded  BOOLEAN,
    PRIMARY KEY (
                 AxisID,
                 HierarchyID
        ),
    FOREIGN KEY (
                 AxisID
        )
        REFERENCES mAxis (AxisID),
    FOREIGN KEY (
                 HierarchyID
        )
        REFERENCES mHierarchy (HierarchyID),
    FOREIGN KEY (
                 HierarchyStartingMemberID
        )
        REFERENCES mMember (MemberID)
);

-- mOrdinateCategorisation
CREATE TABLE mOrdinateCategorisation
(
    OrdinateID               INTEGER,
    DimensionID              INTEGER,
    MemberID                 INTEGER,
    DimensionMemberSignature TEXT,
    Source                   TEXT,
    DPS                      TEXT,
    PRIMARY KEY (
                 OrdinateID,
                 DimensionID
        ),
    FOREIGN KEY (
                 OrdinateID
        )
        REFERENCES mAxisOrdinate (OrdinateID),
    FOREIGN KEY (
                 DimensionID
        )
        REFERENCES mDimension (DimensionID),
    FOREIGN KEY (
                 MemberID
        )
        REFERENCES mMember (MemberID)
);

-- mOwner
CREATE TABLE mOwner
(
    OwnerID        INTEGER,
    OwnerName      TEXT,
    OwnerCode      TEXT,
    OwnerNamespace TEXT,
    OwnerLocation  TEXT,
    OwnerPrefix    TEXT,
    OwnerCopyright TEXT,
    ParentOwnerID  INTEGER,
    ConceptID      INTEGER,
    PRIMARY KEY (
                 OwnerID
        ),
    FOREIGN KEY (
                 ConceptID
        )
        REFERENCES mConcept (ConceptID),
    FOREIGN KEY (
                 ParentOwnerID
        )
        REFERENCES mOwner (OwnerID)
);

-- mOwnerParent
CREATE TABLE mOwnerParent
(
    OwnerID       INTEGER REFERENCES mOwner (OwnerID),
    ParentOwnerID INTEGER REFERENCES mOwner (OwnerID)
);

-- mReference
CREATE TABLE mReference
(
    ReferenceID   INTEGER PRIMARY KEY
        NOT NULL,
    ReferenceType TEXT
);

-- mReferencePart
CREATE TABLE mReferencePart
(
    ReferencePartID INTEGER PRIMARY KEY,
    Name            VARCHAR(200),
    Label           NVARCHAR (2000),
    Description     VARCHAR(2000),
    ConceptID       INTEGER REFERENCES mConcept (ConceptID)
);

-- mReferenceValue
CREATE TABLE mReferenceValue
(
    ReferenceID     INTEGER REFERENCES mReference (ReferenceID),
    ReferencePartID INTEGER REFERENCES mReferencePart (ReferencePartID),
    Value           VARCHAR(2000)
);

-- mRelease
CREATE TABLE mRelease
(
    ReleaseID          INTEGER,
    ReleaseCode        TEXT,
    ReleaseDescription TEXT,
    Status             TEXT,
    PublicationDate               DATE,
    IsCurrent          BOOLEAN,
    ConceptID          INTEGER,
    PRIMARY KEY (
                 ReleaseID
        ),
    FOREIGN KEY (
                 ConceptID
        )
        REFERENCES mConcept (ConceptID)
);

-- mReportingFramework
CREATE TABLE mReportingFramework
(
    FrameworkID    INTEGER,
    FrameworkCode  TEXT,
    FrameworkLabel TEXT,
    ConceptID      INTEGER,
    PRIMARY KEY (
                 FrameworkID
        ),
    FOREIGN KEY (
                 ConceptID
        )
        REFERENCES mConcept (ConceptID)
);

-- mResourceFile
CREATE TABLE mResourceFile
(
    FileID          INTEGER NOT NULL
        CONSTRAINT mLabelsFile_pk PRIMARY KEY,
    Filename        TEXT    NOT NULL,
    ResourceType    TEXT,
    ResourceRoles   TEXT,
    ConfigurationID INTEGER REFERENCES mXbrlExportConfiguration
);

-- mRewriteURI
CREATE TABLE mRewriteURI
(
    RewriteUriID      INTEGER NOT NULL
        CONSTRAINT mRewriteURI_pk PRIMARY KEY,
    Local             TEXT    NOT NULL,
    Absolute          TEXT    NOT NULL,
    TaxonomyPackageID INTEGER REFERENCES mTaxonomyPackage
);

-- mTable
CREATE TABLE mTable
(
    TableID                 INTEGER NOT NULL,
    TableCode               TEXT,
    TableLabel              TEXT,
    FromDate                DATE,
    ToDate                  DATE,
    XbrlFilingIndicatorCode TEXT,
    XbrlTableCode           TEXT,
    ConceptID               INTEGER,
    YDimVal                 TEXT,
    ZDimVal                 TEXT,
    JsonBlob                BLOB,
    PRIMARY KEY (
                 TableID
        ),
    FOREIGN KEY (
                 ConceptID
        )
        REFERENCES mConcept (ConceptID)
);

-- mTableAxis
CREATE TABLE mTableAxis
(
    AxisID  INTEGER NOT NULL,
    TableID INTEGER NOT NULL,
    [Order]
    INTEGER
    NOT
    NULL,
    CONSTRAINT PK_mTableAxis PRIMARY KEY (
                                          AxisID,
                                          TableID
        ),
    FOREIGN KEY (
                 TableID
        )
        REFERENCES mTable (TableID),
    FOREIGN KEY (
                 AxisID
        )
        REFERENCES mAxis (AxisID)
);

-- mTableCell
CREATE TABLE mTableCell
(
    CellID             INTEGER,
    TableID            INTEGER,
    IsRowKey           BOOLEAN,
    IsShaded           BOOLEAN,
    BusinessCode       TEXT,
    DatapointSignature TEXT,
    DPS                TEXT,
    PRIMARY KEY (
                 CellID
        ),
    FOREIGN KEY (
                 TableID
        )
        REFERENCES mTable (TableID)
);

-- mTaxonomy
CREATE TABLE mTaxonomy
(
    TaxonomyID        INTEGER,
    FrameworkID       INTEGER,
    TaxonomyCode      TEXT,
    TaxonomyLabel     TEXT,
    Version           TEXT,
    PublicationDate   DATE,
    TechnicalStandard TEXT,
    ConceptID         INTEGER,
    FromDate          DATE,
    ToDate            DATE,
    ExcelTemplate     BLOB,
    PRIMARY KEY (
                 TaxonomyID
        ),
    FOREIGN KEY (
                 ConceptID
        )
        REFERENCES mConcept (ConceptID),
    FOREIGN KEY (
                 FrameworkID
        )
        REFERENCES mReportingFramework (FrameworkID)
);

-- mTaxonomyPackage
CREATE TABLE mTaxonomyPackage
(
    TaxonomyPackageID INTEGER NOT NULL
        PRIMARY KEY,
    Lang              TEXT,
    SchemaLocation    TEXT,
    Identifier        TEXT,
    Description       TEXT,
    Name              TEXT,
    Version           TEXT,
    Publisher         TEXT,
    PublisherURL      TEXT,
    PublisherCountry  TEXT,
    PublicationDate   DATE,
    LicenceName       TEXT,
    LicenceHref       TEXT,
    CopyrightComment  TEXT
);

-- mTaxonomyTable
CREATE TABLE mTaxonomyTable
(
    TaxonomyID       INTEGER NOT NULL,
    TableID          INTEGER NOT NULL,
    AnnotatedTableID INTEGER NOT NULL,
    IsSimplyReuse    BOOLEAN,
    IsTableSource    BOOLEAN,
    PRIMARY KEY (
                 TaxonomyID,
                 TableID,
                 AnnotatedTableID
        ),
    FOREIGN KEY (
                 TaxonomyID
        )
        REFERENCES mTaxonomy (TaxonomyID) ON UPDATE CASCADE,
    FOREIGN KEY (
                 TableID
        )
        REFERENCES mTable (TableID),
    FOREIGN KEY (
                 AnnotatedTableID
        )
        REFERENCES mTemplateOrTable (TemplateOrTableID)
);

-- mTemplateOrTable
CREATE TABLE mTemplateOrTable
(
    TemplateOrTableID       INTEGER NOT NULL,
    TaxonomyID              INTEGER,
    TemplateOrTableCode     TEXT,
    TemplateOrTableLabel    TEXT,
    TemplateOrTableType     TEXT,
    [Order]
    INTEGER,
    Level                   INTEGER,
    ParentTemplateOrTableID INTEGER,
    ConceptID               INTEGER,
    IsTableGroupSource      BOOLEAN DEFAULT 1,
    TC                      TEXT,
    TT                      TEXT,
    TL                      TEXT,
    TD                      TEXT,
    YC                      TEXT,
    XC                      TEXT,
    PRIMARY KEY (
                 TemplateOrTableID
        ),
    FOREIGN KEY (
                 ConceptID
        )
        REFERENCES mConcept (ConceptID),
    FOREIGN KEY (
                 ParentTemplateOrTableID
        )
        REFERENCES mTemplateOrTable (TemplateOrTableID),
    FOREIGN KEY (
                 TaxonomyID
        )
        REFERENCES mTaxonomy (TaxonomyID) ON UPDATE CASCADE
);

-- mXbrlExportConfiguration
CREATE TABLE mXbrlExportConfiguration
(
    ConfigurationID                   INTEGER NOT NULL
        CONSTRAINT mXbrlExportConfiguration_pk PRIMARY KEY,
    SchemaFile                        TEXT    NOT NULL,
    TechnicalImports                  TEXT,
    IsEntrypoint                      BOOLEAN DEFAULT false,
    IncludeLinkbaseReferencesInOutput BOOLEAN DEFAULT false,
    PresentationLinkbaseFile          TEXT,
    CalculationLinkbaseFile           TEXT,
    DefinitionLinkbaseFile            TEXT,
    RoleIDPattern                     TEXT,
    RoleURIPattern                    TEXT,
    RoleDefinitionGenericLabelPattern TEXT,
    IncludeDefaultMembers             BOOLEAN DEFAULT false,
    DefaultMembersRoleID              TEXT,
    DefaultMembersRoleURI             TEXT,
    DefaultMembersRoleDefinition      TEXT,
    OwnerID                           INTEGER REFERENCES mOwner (OwnerID)
);

-- vValidationRuleExpressions
CREATE TABLE vValidationRuleExpressions
(
    ValidationID    INTEGER PRIMARY KEY,
    ValidationCode  TEXT,
    ErrorMessage    TEXT,
    Rule            TEXT,
    Filter          TEXT,
    Prerequisites   TEXT,
    [Join]
    TEXT,
    Scope           TEXT,
    SQL             TEXT,
    IsEnabled       BOOLEAN DEFAULT 1 NOT NULL,
    IsPoint         BOOLEAN DEFAULT 0 NOT NULL,
    AlwaysOn        BOOLEAN DEFAULT 0 NOT NULL,
    IncludeInXBRL   BOOLEAN DEFAULT 1 NOT NULL,
    ExpertMode   BOOLEAN DEFAULT 0 NOT NULL,
    PublishToParticles    BOOLEAN DEFAULT 1 NOT NULL,
    ToleranceMargin TEXT,
    VariableNames   TEXT,
    Precondition   TEXT,
    ConceptID       INTEGER           NOT NULL,
    SourceTaxonomyID       INTEGER           NOT NULL,
    FOREIGN KEY (
                 ConceptID
        )
        REFERENCES mConcept (ConceptID)
    FOREIGN KEY (
                 SourceTaxonomyID
        )
        REFERENCES mTaxonomy (TaxonomyID)
);

-- vValidationRuleTables
CREATE TABLE vValidationRuleTables
(
    ValidationID INTEGER,
    ModuleID     INTEGER,
    TableID      INTEGER,
    Severity     TEXT,
    IsValidationSource BOOLEAN,
    PRIMARY KEY (
                 ValidationID,
                 ModuleID,
                 TableID
        )
);

