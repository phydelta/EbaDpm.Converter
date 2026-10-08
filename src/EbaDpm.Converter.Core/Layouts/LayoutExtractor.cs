using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using EbaDpm.Converter.Core.Layouts.Xlsx;

namespace EbaDpm.Converter.Core.Layouts;

/// <summary>Final tally of an extraction, so that <c>--extract-layouts</c> reports it on the
/// console: <c>LayoutUnparsed</c> must ALWAYS be announced, whether it is 0 or thousands.</summary>
public sealed record LayoutExtractionSummary(
    int FilesProcessed,
    int SheetsProcessed,
    int DeclarationsWritten,
    int OrdinatesWritten,
    int ValuesWritten,
    int RawCellsWritten,
    int UnparsedWritten,
    IReadOnlyList<(string Reason, int Count)> UnparsedReasonSample,
    bool DictionaryUsed,
    IReadOnlyList<(string Kind, int Count)> UnparsedByKind,
    int OrdinateDatapointSlotMismatches,
    string? DictionaryPath,
    string? DictionarySha256,
    int XAxisOrdinatesTotal,
    int XAxisOrdinatesWithRealCode,
    IReadOnlyList<(string TableCode, int Count)> XAxisFallbackSample,
    int XAxisOrdinatesWithoutRowInSheetsThatHaveRows,
    int SheetsWithOpenRowAxisConfirmedByDeclaration,
    int SheetsWithOpenRowAxisAssumedByAbsenceOnly,
    int ValuesLinkedToOrdinate,
    int ValuesUnlinkedToOrdinate,
    int CellsWritten,
    int ShadedCellsWritten,
    int EqualityCommentsExamined,
    int EqualityRuleLinesFound,
    int EqualityRulesWritten,
    int EqualityRuleGrammarFailures);

/// <summary>
/// Orchestrates the extraction of a directory of Annotated Table Layouts into a queryable SQLite
/// repository (<see cref="LayoutRepositorySchema"/>). It walks the directory recursively looking
/// for <c>*.xlsx</c> — so it covers both 4.2 (subfolders per framework) and 4.3 (loose files)
/// without distinguishing cases.
///
/// <paramref name="dictionaryPath"/> is OPTIONAL at this level (Core): several tests extract
/// deliberately WITHOUT a dictionary to document what is lost without it. The CLI is what makes it
/// mandatory (<c>--extract-layouts</c> requires <c>--dictionary</c>) — the library keeps the
/// flexibility so the difference can be MEASURED, not to recommend using it without a dictionary.
/// </summary>
public static class LayoutExtractor
{
    public static LayoutExtractionSummary Extract(
        string inputDirectory, string outputPath, bool overwrite, string? dictionaryPath)
    {
        if (!Directory.Exists(inputDirectory))
        {
            throw new DirectoryNotFoundException($"The layouts directory does not exist: '{inputDirectory}'.");
        }

        if (File.Exists(outputPath))
        {
            if (!overwrite)
            {
                throw new IOException($"The output file already exists: {outputPath}. Use --overwrite.");
            }

            File.Delete(outputPath);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var files = Directory.GetFiles(inputDirectory, "*.xlsx", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();

        using var dictionary = new LayoutDictionary(dictionaryPath);
        var dictionarySha256 = dictionaryPath is not null ? ComputeSha256(dictionaryPath) : null;

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = outputPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            // foreign_keys OFF during the load: bulk-load performance. LayoutCell.RowOrdinateId/
            // ColumnOrdinateId reference ordinates of the SAME sheet that were fully inserted
            // before the first cell is processed, so no pending reference needs resolving.
            pragma.CommandText = "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF; PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        LayoutRepositorySchema.Create(connection);

        using var fileInsert = new PreparedInsert(connection, "LayoutFile", ["FileId", "Path", "Sha256", "FrameworkCode", "ModuleCode", "ReleaseLabel"]);
        using var sheetInsert = new PreparedInsert(connection, "LayoutSheet", ["SheetId", "FileId", "SheetName", "TableCode", "ZSuffix", "TableLabel"]);
        using var declInsert = new PreparedInsert(connection, "LayoutDeclaration", ["DeclId", "SheetId", "Region", "CellRef", "DimensionCode", "DomainCode", "HierarchyCode", "Label", "IsKey"]);
        using var ordinateInsert = new PreparedInsert(connection, "LayoutOrdinate", ["OrdinateId", "SheetId", "Axis", "OrdinateCode", "IsFallbackCode", "Label"]);
        using var cellInsert = new PreparedInsert(connection, "LayoutCell", ["CellId", "SheetId", "RowOrdinateId", "ColumnOrdinateId", "CellRef", "DatapointId", "DataType", "IsShaded"]);
        using var valueInsert = new PreparedInsert(connection, "LayoutValue", ["ValueId", "SheetId", "DeclId", "OrdinateId", "DomainCode", "MemberCode", "Label", "CellRef"]);
        using var rawInsert = new PreparedInsert(connection, "LayoutRaw", ["SheetId", "CellRef", "Value"]);
        using var unparsedInsert = new PreparedInsert(connection, "LayoutUnparsed", ["SheetId", "CellRef", "Value", "Kind", "Reason"]);
        using var extractionInsert = new PreparedInsert(connection, "LayoutExtraction", ["ExtractionId", "DictionaryPath", "DictionarySha256", "ExtractedAtUtc"]);
        using var equalityRuleInsert = new PreparedInsert(
            connection, "LayoutEqualityRule",
            ["RuleId", "Position", "SourceFile", "SheetId", "CellRef", "Term", "TableCode", "RowCode", "ColumnCode", "ZCode"]);

        var transaction = connection.BeginTransaction();
        AssignTransaction(transaction, fileInsert, sheetInsert, declInsert, ordinateInsert, cellInsert, valueInsert, rawInsert, unparsedInsert, extractionInsert, equalityRuleInsert);

        // Provenance: which dictionary resolved this extraction. One row, always — with a null
        // dictionaryPath if none was given, which is just as real a piece of information.
        extractionInsert.Insert(1, dictionaryPath, dictionarySha256, DateTime.UtcNow.ToString("o"));

        var fileId = 0;
        var sheetId = 0;
        var declId = 0;
        var ordinateId = 0;
        var cellId = 0;
        var valueId = 0;

        var sheetsProcessed = 0;
        var declarationsWritten = 0;
        var ordinatesWritten = 0;
        var cellsWritten = 0;
        var shadedCellsWritten = 0;
        var valuesWritten = 0;
        var rawCellsWritten = 0;
        var unparsedWritten = 0;
        var datapointSlotMismatches = 0;
        var unparsedReasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var unparsedKindCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var xAxisTotal = 0;
        var xAxisRealCode = 0;
        var xAxisFallbackByTableCode = new Dictionary<string, int>(StringComparer.Ordinal);

        // The invariant "RowOrdinateId is only NULL in sheets without any Y ordinate" holds TODAY
        // only because the counter-example pattern is absent (a datapoint-shaped cell in the HEADER
        // region of a sheet that DOES have row ordinates — ParseHeaderRow calls
        // RegisterDatapointOrdinate exactly there, and nothing prevents it in a future release).
        // It is counted explicitly and ALWAYS reported, even when it is 0.
        var xAxisWithoutRowInSheetsThatHaveRows = 0;

        // How many of the sheets without any row (Y) ordinate have DIRECT confirmation in the
        // layout itself —an IsKey declaration in the "Rows" region (the "Open Rows" case)— versus
        // how many are taken for granted only because there is no Y at all (absence as a proxy,
        // not as proof). Purely informative: it changes neither what is written to any table nor
        // the value of xAxisWithoutRowInSheetsThatHaveRows. See LayoutAxis: the underlying
        // asymmetry —RowOrdinateId only exists on column cells, there is no equivalent field for
        // an open COLUMN axis— is not resolved here.
        var sheetsRowAxisOpenConfirmedByDeclaration = 0;
        var sheetsRowAxisOpenAssumedByAbsenceOnly = 0;

        // Accounting for the value -> ordinate link. The rule is implemented in
        // LayoutValueOrdinateLinker — it is still always reported, including any 0 rows there
        // would be if something broke.
        var valuesLinkedToOrdinate = 0;
        var valuesUnlinkedToOrdinate = 0;

        // The "==" equality rules of the cell comments. ruleIdByCanonical lives OUTSIDE the file
        // loop on purpose — a rule may (and usually does) be annotated in cells of TWO different
        // files (two tables of two modules), and deduplication has to see the whole corpus, not
        // file by file.
        var ruleIdByCanonical = new Dictionary<string, int>(StringComparer.Ordinal);
        var equalityRuleId = 0;
        var equalityCommentsExamined = 0;
        var equalityRuleLinesFound = 0;
        var equalityRulesWritten = 0;
        var equalityRuleGrammarFailures = 0;

        foreach (var filePath in files)
        {
            fileId++;
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
            var fileNameInfo = LayoutFileNameParser.Parse(nameWithoutExtension);
            var sha256 = ComputeSha256(filePath);

            fileInsert.Insert(fileId, filePath, sha256, fileNameInfo.FrameworkCode, fileNameInfo.ModuleCode, fileNameInfo.ReleaseLabel);

            var workbook = XlsxReader.Read(filePath);
            foreach (var sheet in workbook.Sheets)
            {
                if (string.Equals(sheet.Name, "TOC", StringComparison.Ordinal))
                {
                    continue;
                }

                sheetId++;
                sheetsProcessed++;

                var (tableCode, zSuffix) = LayoutSheetNameParser.Parse(sheet.Name);
                var titleText = sheet.Cells.TryGetValue((1, 1), out var a1) ? a1 : null;
                var tableLabel = LayoutSheetNameParser.ExtractTableLabel(titleText);

                sheetInsert.Insert(sheetId, fileId, sheet.Name, tableCode, zSuffix, tableLabel);

                var result = LayoutSheetParser.Parse(sheet, dictionary);
                datapointSlotMismatches += result.DatapointSlotMismatches;

                foreach (var (coords, value) in result.Raw)
                {
                    rawInsert.Insert(sheetId, CellRefUtil.ToCellRef(coords.Row, coords.Col), value);
                    rawCellsWritten++;
                }

                var declIds = new Dictionary<DeclarationBuilder, int>();
                foreach (var decl in result.Declarations)
                {
                    declId++;
                    declIds[decl] = declId;
                    declInsert.Insert(declId, sheetId, decl.Region, decl.CellRef, decl.DimensionCode, decl.DomainCode, decl.HierarchyCode, decl.Label, decl.IsKey ? 1 : 0);
                    declarationsWritten++;
                }

                // result.Ordinates already comes deduplicated by (Axis, OrdinateCode) from
                // LayoutSheetParser (one row per X-axis column seeded from the code row, one per
                // Y-axis row) — a single pass is enough (RowOrdinateId does not live in
                // LayoutOrdinate, see LayoutCell).
                var ordinateIds = new Dictionary<OrdinateBuilder, int>();
                foreach (var ordinate in result.Ordinates)
                {
                    ordinateId++;
                    ordinateIds[ordinate] = ordinateId;
                    ordinateInsert.Insert(ordinateId, sheetId, ordinate.Axis, ordinate.OrdinateCode, ordinate.IsFallbackCode ? 1 : 0, ordinate.Label);
                    ordinatesWritten++;

                    if (ordinate.Axis == LayoutAxis.Column)
                    {
                        xAxisTotal++;
                        if (ordinate.IsFallbackCode)
                        {
                            xAxisFallbackByTableCode[tableCode] = xAxisFallbackByTableCode.GetValueOrDefault(tableCode) + 1;
                        }
                        else
                        {
                            xAxisRealCode++;
                        }
                    }
                }

                // "No row ordinates" is used as a PROXY for "open row axis" — it is the only form
                // this pattern has taken so far, but it is an assumption of absence, not proof.
                // When it exists, the DIRECT signal in the layout is an IsKey declaration in the
                // "Rows" region (the "Open Rows" of C_106.00 itself) — it is checked here and
                // counted separately, without changing the criterion that
                // xAxisWithoutRowInSheetsThatHaveRows uses below.
                var sheetHasRowOrdinates = result.RowOrdinates.Count > 0;
                if (!sheetHasRowOrdinates)
                {
                    var confirmedByDeclaration = result.Declarations.Any(
                        d => d.IsKey && string.Equals(d.Region, "Rows", StringComparison.Ordinal));
                    if (confirmedByDeclaration)
                    {
                        sheetsRowAxisOpenConfirmedByDeclaration++;
                    }
                    else
                    {
                        sheetsRowAxisOpenAssumedByAbsenceOnly++;
                    }
                }

                // LayoutCell — the row x column cartesian product. All the ordinates of this sheet
                // already have an OrdinateId assigned (loop above), so ColumnOrdinateId/
                // RowOrdinateId resolve without any pending reference.
                foreach (var cell in result.Cells)
                {
                    if (cell.RowOrdinate is null && sheetHasRowOrdinates)
                    {
                        xAxisWithoutRowInSheetsThatHaveRows++;
                    }

                    cellId++;
                    var rowOrdinateId = cell.RowOrdinate is not null ? ordinateIds[cell.RowOrdinate] : (int?)null;
                    cellInsert.Insert(
                        cellId, sheetId, rowOrdinateId, ordinateIds[cell.ColumnOrdinate], cell.CellRef,
                        cell.DatapointId, cell.DataType, cell.IsShaded ? 1 : 0);
                    cellsWritten++;
                    if (cell.IsShaded)
                    {
                        shadedCellsWritten++;
                    }
                }

                // Resolution index for this sheet and single point of value -> ordinate linking —
                // see LayoutValueOrdinateLinker (Rows->X by column, Columns->Y by row,
                // Header->Z with no ordinate by design).
                var ordinateIndex = LayoutOrdinateIndex.Build(result.Ordinates, ordinateIds);

                foreach (var value in result.Values)
                {
                    valueId++;
                    var link = LayoutValueOrdinateLinker.Resolve(value, result, ordinateIndex);
                    valueInsert.Insert(valueId, sheetId, declIds[value.Declaration], link.OrdinateId, value.DomainCode, value.MemberCode, value.Label, value.CellRef);
                    valuesWritten++;

                    if (link.IsLinked)
                    {
                        valuesLinkedToOrdinate++;
                    }
                    else
                    {
                        valuesUnlinkedToOrdinate++;

                        // No LayoutValue is silently discarded. One that does not link ends up in
                        // LayoutUnparsed with Kind=UnlinkedValue and the failure reason — which
                        // distinguishes "the rule does not apply" (Z axis, by design) from "the
                        // code does not exist on that axis" (ordinate extraction gap).
                        var reason = link.FailureReason ?? "reason not reported by LayoutValueOrdinateLinker";
                        var (unlinkedRow, unlinkedCol) = CellRefUtil.Parse(value.CellRef);
                        var unlinkedRawText = result.Raw.GetValueOrDefault((unlinkedRow, unlinkedCol), value.Label ?? string.Empty);
                        unparsedInsert.Insert(sheetId, value.CellRef, unlinkedRawText, UnparsedKind.UnlinkedValue, reason);
                        unparsedWritten++;
                        unparsedReasonCounts[reason] = unparsedReasonCounts.GetValueOrDefault(reason) + 1;
                        unparsedKindCounts[UnparsedKind.UnlinkedValue] = unparsedKindCounts.GetValueOrDefault(UnparsedKind.UnlinkedValue) + 1;
                    }
                }

                foreach (var unparsed in result.Unparsed)
                {
                    unparsedInsert.Insert(sheetId, unparsed.CellRef, unparsed.Value, unparsed.Kind, unparsed.Reason);
                    unparsedWritten++;
                    unparsedReasonCounts[unparsed.Reason] = unparsedReasonCounts.GetValueOrDefault(unparsed.Reason) + 1;
                    unparsedKindCounts[unparsed.Kind] = unparsedKindCounts.GetValueOrDefault(unparsed.Kind) + 1;
                }

                // The "==" equality rules of the cell comments -- an annotation PARALLEL to the
                // grid, unrelated to LayoutSheetParser (which never opens xl/comments*.xml). It is
                // processed per sheet, over the comments that XlsxReader has already resolved
                // (_x000D_ -> '\n').
                foreach (var (coords, commentText) in sheet.Comments)
                {
                    equalityCommentsExamined++;
                    var commentCellRef = CellRefUtil.ToCellRef(coords.Row, coords.Col);

                    foreach (var rawLine in LayoutEqualityRuleParser.ExtractRawLines(commentText))
                    {
                        equalityRuleLinesFound++;

                        var structured = rawLine.RawTerms.Select(LayoutEqualityRuleParser.Parse).ToList();
                        if (structured.Any(t => t is null))
                        {
                            // A line with "==" whose terms do not all structure is not silently
                            // discarded -- it is a rule GRAMMAR failure. It has not been seen in the
                            // 4.2 layouts, but it is not assumed to stay that way in another release.
                            unparsedInsert.Insert(
                                sheetId, commentCellRef, rawLine.Line, UnparsedKind.EqualityRuleGrammarFailure,
                                "equality rule term that does not follow the form {table, r<row>, c <column>[, s<Z>]}");
                            unparsedWritten++;
                            equalityRuleGrammarFailures++;
                            unparsedKindCounts[UnparsedKind.EqualityRuleGrammarFailure] =
                                unparsedKindCounts.GetValueOrDefault(UnparsedKind.EqualityRuleGrammarFailure) + 1;
                            continue;
                        }

                        // Deduplication by SET of raw terms: the EBA annotates the SAME rule, in
                        // full, in the comment of each participating cell — it is only written
                        // the first time it is seen.
                        var canonicalKey = LayoutEqualityRuleParser.CanonicalKey(rawLine.RawTerms);
                        if (!ruleIdByCanonical.ContainsKey(canonicalKey))
                        {
                            equalityRuleId++;
                            ruleIdByCanonical[canonicalKey] = equalityRuleId;
                            equalityRulesWritten++;

                            // CANONICAL order (alphabetical by raw term, like the deduplication key)
                            // so that Position is deterministic regardless of which cell of which
                            // table the rule was found in first.
                            var ordered = structured
                                .Select((t, i) => (Term: t!.Value, Raw: rawLine.RawTerms[i]))
                                .OrderBy(x => x.Raw, StringComparer.Ordinal)
                                .ToList();

                            for (var position = 0; position < ordered.Count; position++)
                            {
                                var (term, raw) = ordered[position];
                                equalityRuleInsert.Insert(
                                    equalityRuleId, position, filePath, sheetId, commentCellRef, raw,
                                    term.TableCode, term.RowCode, term.ColumnCode, term.ZCode);
                            }
                        }
                    }
                }
            }

            // Commit every 10 files: bounds the duration of the transaction without needing one
            // per sheet.
            if (fileId % 10 == 0)
            {
                transaction.Commit();
                transaction.Dispose();
                transaction = connection.BeginTransaction();
                AssignTransaction(transaction, fileInsert, sheetInsert, declInsert, ordinateInsert, cellInsert, valueInsert, rawInsert, unparsedInsert, extractionInsert, equalityRuleInsert);
            }
        }

        transaction.Commit();
        transaction.Dispose();

        var reasonSample = unparsedReasonCounts
            .OrderByDescending(kv => kv.Value)
            .Take(20)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();

        var kindBreakdown = unparsedKindCounts
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();

        var xAxisFallbackSample = xAxisFallbackByTableCode
            .OrderByDescending(kv => kv.Value)
            .Take(20)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();

        return new LayoutExtractionSummary(
            files.Count, sheetsProcessed, declarationsWritten, ordinatesWritten, valuesWritten,
            rawCellsWritten, unparsedWritten, reasonSample, dictionary.IsPresent, kindBreakdown,
            datapointSlotMismatches, dictionaryPath, dictionarySha256,
            xAxisTotal, xAxisRealCode, xAxisFallbackSample, xAxisWithoutRowInSheetsThatHaveRows,
            sheetsRowAxisOpenConfirmedByDeclaration, sheetsRowAxisOpenAssumedByAbsenceOnly,
            valuesLinkedToOrdinate, valuesUnlinkedToOrdinate, cellsWritten, shadedCellsWritten,
            equalityCommentsExamined, equalityRuleLinesFound, equalityRulesWritten, equalityRuleGrammarFailures);
    }

    private static void AssignTransaction(SqliteTransaction transaction, params PreparedInsert[] inserts)
    {
        foreach (var insert in inserts)
        {
            insert.Transaction = transaction;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }
}
