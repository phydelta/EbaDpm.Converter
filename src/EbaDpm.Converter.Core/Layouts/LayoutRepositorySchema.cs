using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Layouts;

/// <summary>
/// Creates the schema of the layouts REPOSITORY: it is not the target distribution schema — it is
/// a separate instrument that dumps the Annotated Table Layouts into something queryable by SQL,
/// so it can be joined against the converter output in plane C.
///
/// <c>FileId</c>/<c>SheetId</c>/<c>DeclId</c>/<c>OrdinateId</c>/<c>ValueId</c>/<c>CellId</c> are
/// explicit <c>INTEGER PRIMARY KEY</c> columns (aliases of <c>rowid</c>, so they can be referenced
/// by FK). <c>LayoutUnparsed.Kind</c> mechanically distinguishes the reasons (grammar failure,
/// orphan value, orphan declaration and a value NOT LINKED to any ordinate — <c>UnlinkedValue</c>,
/// see <c>LayoutValueOrdinateLinker</c>) without requiring a forensic query. <c>LayoutExtraction</c>
/// holds one PROVENANCE row per extraction: which dictionary (path and SHA-256) was used to resolve
/// single-code ambiguity and to validate two-term codes, because without that record the repository
/// does not say what it was resolved against. <c>LayoutOrdinate.IsFallbackCode</c> marks explicitly
/// when <c>OrdinateCode</c> is a last resort rather than the code of the header row: a column
/// letter must not be indistinguishable from a real DPM code (2-6 digits).
///
/// <c>LayoutOrdinate</c> stores one row per ORDINATE — exactly one per
/// <c>(SheetId, Axis, OrdinateCode)</c> — not one row per CELL. <c>RowOrdinateId</c>,
/// <c>DatapointId</c>, <c>DataType</c> and <c>CellRef</c> are properties of the CELL, not of the
/// ordinate: ordinates live on the axes, cells are their product. Those columns live in
/// <c>LayoutCell</c>.
///
/// <c>LayoutCell</c> is the COMPLETE cartesian product of the row and column ordinates of a sheet,
/// not just the cells where a datapoint exists. A shaded cell by definition NEVER carries a
/// datapoint, so populating <c>LayoutCell</c> only where a datapoint identifier was found would
/// leave <c>IsShaded</c> always <c>0</c>: a field that can never be <c>1</c> is not a result.
/// <c>DatapointId</c>/<c>DataType</c> are <c>NULL</c> where there is no data (shaded or not — they
/// are two independent measures of the same cell) and <c>IsShaded</c> is read from the Excel
/// regardless of whether there is data. <c>RowOrdinateId</c> is <c>NULL</c> only when the sheet
/// has NO <c>Y</c> ordinate at all (open row axis) — there is then no fixed set of ordinates to
/// generate the product from, so the cell is kept as the parsing found it.
/// </summary>
internal static class LayoutRepositorySchema
{
    private const string Ddl =
        """
        CREATE TABLE LayoutFile (
            FileId        INTEGER PRIMARY KEY,
            Path          TEXT NOT NULL,
            Sha256        TEXT NOT NULL,
            FrameworkCode TEXT NOT NULL,
            ModuleCode    TEXT NULL,
            ReleaseLabel  TEXT NOT NULL
        );

        CREATE TABLE LayoutSheet (
            SheetId    INTEGER PRIMARY KEY,
            FileId     INTEGER NOT NULL REFERENCES LayoutFile (FileId),
            SheetName  TEXT NOT NULL,
            TableCode  TEXT NOT NULL,
            ZSuffix    TEXT NULL,
            TableLabel TEXT NULL
        );

        CREATE TABLE LayoutDeclaration (
            DeclId        INTEGER PRIMARY KEY,
            SheetId       INTEGER NOT NULL REFERENCES LayoutSheet (SheetId),
            Region        TEXT NOT NULL,
            CellRef       TEXT NOT NULL,
            DimensionCode TEXT NULL,
            DomainCode    TEXT NULL,
            HierarchyCode TEXT NULL,
            Label         TEXT NULL,
            IsKey         INTEGER NOT NULL DEFAULT 0
        );

        -- ONE row per (SheetId, Axis, OrdinateCode). Whatever is a property of the CELL lives in
        -- LayoutCell.
        CREATE TABLE LayoutOrdinate (
            OrdinateId     INTEGER PRIMARY KEY,
            SheetId        INTEGER NOT NULL REFERENCES LayoutSheet (SheetId),
            Axis           TEXT NOT NULL,
            OrdinateCode   TEXT NOT NULL,
            IsFallbackCode INTEGER NOT NULL DEFAULT 0,
            Label          TEXT NULL
        );

        -- The COMPLETE cartesian product of the row and column ordinates of the sheet — one row
        -- for EACH combination, with or without a datapoint, shaded or not. DatapointId/DataType
        -- are NULL where there is no data (they are NULL in shaded cells by definition — do not
        -- confuse "no data" with "shaded": they are two independent measures!).
        -- RowOrdinateId is NULL only when the sheet has NO Y ordinate at all (open row axis) —
        -- there is then no product to generate and the cell is kept as the parsing saw it.
        -- IsShaded: read from xl/styles.xml, not inferred — see LayoutSheetParser/Xlsx.XlsxReader.
        CREATE TABLE LayoutCell (
            CellId           INTEGER PRIMARY KEY,
            SheetId          INTEGER NOT NULL REFERENCES LayoutSheet (SheetId),
            RowOrdinateId    INTEGER NULL REFERENCES LayoutOrdinate (OrdinateId),
            ColumnOrdinateId INTEGER NOT NULL REFERENCES LayoutOrdinate (OrdinateId),
            CellRef          TEXT NOT NULL,
            DatapointId      TEXT NULL,
            DataType         TEXT NULL,
            IsShaded         INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE LayoutValue (
            ValueId    INTEGER PRIMARY KEY,
            SheetId    INTEGER NOT NULL REFERENCES LayoutSheet (SheetId),
            DeclId     INTEGER NOT NULL REFERENCES LayoutDeclaration (DeclId),
            OrdinateId INTEGER NULL REFERENCES LayoutOrdinate (OrdinateId),
            DomainCode TEXT NULL,
            MemberCode TEXT NULL,
            Label      TEXT NULL,
            CellRef    TEXT NOT NULL
        );

        CREATE TABLE LayoutRaw (
            SheetId INTEGER NOT NULL REFERENCES LayoutSheet (SheetId),
            CellRef TEXT NOT NULL,
            Value   TEXT NOT NULL,
            PRIMARY KEY (SheetId, CellRef)
        );

        CREATE TABLE LayoutUnparsed (
            SheetId INTEGER NOT NULL REFERENCES LayoutSheet (SheetId),
            CellRef TEXT NOT NULL,
            Value   TEXT NOT NULL,
            Kind    TEXT NOT NULL,
            Reason  TEXT NOT NULL
        );

        -- One row per TERM of each "==" equality rule read from xl/comments*.xml, deduplicated
        -- by the SET of its terms (the EBA repeats the same rule, in full, in the comment of EACH
        -- participating cell). SourceFile/SheetId/CellRef are the PROVENANCE (where the rule was
        -- first found) -- the resolution of each term against the output does not happen here, it
        -- is done in plane C (PlaneCEqualityChecker) against mTable/mAxisOrdinate/
        -- mCellPosition/mTableCell of THE OUTPUT, never against this repository.
        CREATE TABLE LayoutEqualityRule (
            RuleId     INTEGER NOT NULL,
            Position   INTEGER NOT NULL,
            SourceFile TEXT NOT NULL,
            SheetId    INTEGER NOT NULL REFERENCES LayoutSheet (SheetId),
            CellRef    TEXT NOT NULL,
            Term       TEXT NOT NULL,
            TableCode  TEXT NOT NULL,
            RowCode    TEXT NOT NULL,
            ColumnCode TEXT NOT NULL,
            ZCode      TEXT NULL,
            PRIMARY KEY (RuleId, Position)
        );

        CREATE TABLE LayoutExtraction (
            ExtractionId     INTEGER PRIMARY KEY,
            DictionaryPath   TEXT NULL,
            DictionarySha256 TEXT NULL,
            ExtractedAtUtc   TEXT NOT NULL
        );

        CREATE INDEX IX_LayoutSheet_File ON LayoutSheet (FileId);
        CREATE INDEX IX_LayoutSheet_TableCode ON LayoutSheet (TableCode, ZSuffix);
        CREATE INDEX IX_LayoutDeclaration_Sheet ON LayoutDeclaration (SheetId);
        CREATE INDEX IX_LayoutOrdinate_Sheet ON LayoutOrdinate (SheetId);
        -- Deliberately NOT UNIQUE: the construction (LayoutSheetParser) yields one row per
        -- (SheetId, Axis, OrdinateCode) for the layouts seen so far (every column and row had a
        -- unique code), but "the same code in two different columns/rows of one sheet" is not
        -- ruled out — an unverified invariant is not turned into a constraint that could abort the
        -- whole extraction.
        CREATE INDEX IX_LayoutOrdinate_Axis_Code ON LayoutOrdinate (SheetId, Axis, OrdinateCode);
        CREATE INDEX IX_LayoutCell_Sheet ON LayoutCell (SheetId);
        CREATE INDEX IX_LayoutCell_RowOrdinate ON LayoutCell (RowOrdinateId);
        CREATE INDEX IX_LayoutCell_ColumnOrdinate ON LayoutCell (ColumnOrdinateId);
        -- UNIQUE: by construction (claimed-set in LayoutSheetParser) a cell is registered at
        -- most once.
        CREATE UNIQUE INDEX IX_LayoutCell_Sheet_CellRef ON LayoutCell (SheetId, CellRef);
        CREATE INDEX IX_LayoutValue_Sheet ON LayoutValue (SheetId);
        CREATE INDEX IX_LayoutValue_Decl ON LayoutValue (DeclId);
        CREATE INDEX IX_LayoutUnparsed_Sheet ON LayoutUnparsed (SheetId);
        CREATE INDEX IX_LayoutUnparsed_Kind ON LayoutUnparsed (Kind);
        CREATE INDEX IX_LayoutEqualityRule_Rule ON LayoutEqualityRule (RuleId);
        CREATE INDEX IX_LayoutEqualityRule_TableCode ON LayoutEqualityRule (TableCode);
        """;

    public static void Create(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Ddl;
        command.ExecuteNonQuery();
    }
}
