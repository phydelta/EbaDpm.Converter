using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.PlaneC;

/// <summary>A signature rebuilt from ONE non-shaded layout cell.
/// <paramref name="SheetId"/> is kept so the report can NAME the sheets to which the release
/// tolerance <c>REL.n -> REL</c> was applied.</summary>
public sealed record LayoutCellSignature(
    long SheetId, string TableCode, string FrameworkCode, string ReleaseLabel, IReadOnlyList<string> NormalizedTerms);

/// <summary>Result of reading the whole layouts repository for plane C.</summary>
public sealed record PlaneCLayoutData(
    IReadOnlyList<LayoutCellSignature> Signatures,
    long NonShadedCellsExamined,
    long CellsWithEmptySignature);

/// <summary>
/// Rebuilds the <c>DatapointSignature</c> from the layouts repository: for each NON-shaded
/// <c>LayoutCell</c>, the union of the values of its <c>RowOrdinateId</c>, its
/// <c>ColumnOrdinateId</c> and the SHEET-level ones (<c>LayoutValue.OrdinateId IS NULL</c>), plus
/// (rule R2) a <c>&lt;dimension&gt;(*)</c> term for every <c>LayoutDeclaration.IsKey = 1</c> of the
/// sheet, added to ALL its cells: a key declaration does not produce a value, it DECLARES (426
/// declarations, 0 <c>LayoutValue</c> rows).
///
/// The dimension token of a declaration is <c>DimensionCode</c> if it has one (normal two-code
/// form, or Main Property -> <c>"MET"</c> via <c>DomainCode</c>) and otherwise <c>DomainCode</c>,
/// which is where the dimension code ends up in the MERGED single-cell form
/// "(domain:hierarchy) ... &lt;Key value&gt;" (<c>LayoutSheetParser</c>, comment of
/// <c>BuildTwoCodeDeclaration</c>).
/// </summary>
public static class PlaneCLayoutReader
{
    public static PlaneCLayoutData Read(SqliteConnection layouts)
    {
        var sheetInfo = new Dictionary<long, (string TableCode, string FrameworkCode, string ReleaseLabel)>();
        using (var cmd = layouts.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT s."SheetId", s."TableCode", f."FrameworkCode", f."ReleaseLabel"
                FROM "LayoutSheet" s JOIN "LayoutFile" f ON f."FileId" = s."FileId"
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                sheetInfo[reader.GetInt64(0)] = (reader.GetString(1), reader.GetString(2), reader.GetString(3));
            }
        }

        // DeclId -> (SheetId, normalized dimension token, IsKey).
        var declInfo = new Dictionary<long, (long SheetId, string DimToken, bool IsKey)>();
        using (var cmd = layouts.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT "DeclId", "SheetId", "DimensionCode", "DomainCode", "IsKey"
                FROM "LayoutDeclaration"
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var declId = reader.GetInt64(0);
                var sheetId = reader.GetInt64(1);
                var dimensionCode = reader.IsDBNull(2) ? null : reader.GetString(2);
                var domainCode = reader.IsDBNull(3) ? null : reader.GetString(3);
                var isKey = reader.GetInt64(4) != 0;

                var token = dimensionCode ?? domainCode;
                if (token is null)
                {
                    // Defensive: a declaration with neither dimension nor domain contributes no
                    // term and nothing is guessed; it is silently dropped from the signature (it
                    // cannot happen with the measured grammar, but it is not assumed).
                    continue;
                }

                declInfo[declId] = (sheetId, PlaneCSignature.StripEbaPrefix(token), isKey);
            }
        }

        // (SheetId, OrdinateId) -> terms; SheetId -> sheet-level terms (OrdinateId NULL).
        var termsByOrdinate = new Dictionary<(long SheetId, long OrdinateId), List<string>>();
        var headerTermsBySheet = new Dictionary<long, List<string>>();
        using (var cmd = layouts.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT v."SheetId", v."DeclId", v."OrdinateId", v."MemberCode"
                FROM "LayoutValue" v
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var sheetId = reader.GetInt64(0);
                var declId = reader.GetInt64(1);
                var ordinateId = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
                var memberCode = reader.IsDBNull(3) ? null : reader.GetString(3);

                if (memberCode is null || !declInfo.TryGetValue(declId, out var decl))
                {
                    continue;
                }

                var term = PlaneCSignature.ComposeAndNormalizeTerm(decl.DimToken, memberCode);
                if (ordinateId is { } oid)
                {
                    var key = (sheetId, oid);
                    if (!termsByOrdinate.TryGetValue(key, out var list))
                    {
                        list = [];
                        termsByOrdinate[key] = list;
                    }

                    list.Add(term);
                }
                else
                {
                    if (!headerTermsBySheet.TryGetValue(sheetId, out var list))
                    {
                        list = [];
                        headerTermsBySheet[sheetId] = list;
                    }

                    list.Add(term);
                }
            }
        }

        // R2: one <dimension>(*) term per IsKey=1 declaration, per sheet.
        var keyTermsBySheet = new Dictionary<long, List<string>>();
        foreach (var decl in declInfo.Values.Where(d => d.IsKey))
        {
            var term = PlaneCSignature.NormalizeTerm(decl.DimToken + "(*)");
            if (!keyTermsBySheet.TryGetValue(decl.SheetId, out var list))
            {
                list = [];
                keyTermsBySheet[decl.SheetId] = list;
            }

            list.Add(term);
        }

        var signatures = new List<LayoutCellSignature>();
        long nonShadedExamined = 0;
        long emptySignature = 0;

        using (var cmd = layouts.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT "SheetId", "RowOrdinateId", "ColumnOrdinateId"
                FROM "LayoutCell"
                WHERE "IsShaded" = 0
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                nonShadedExamined++;
                var sheetId = reader.GetInt64(0);
                var rowOrdinateId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                var columnOrdinateId = reader.GetInt64(2);

                if (!sheetInfo.TryGetValue(sheetId, out var sheet))
                {
                    continue;
                }

                var terms = new HashSet<string>(StringComparer.Ordinal);
                if (headerTermsBySheet.TryGetValue(sheetId, out var headerTerms))
                {
                    terms.UnionWith(headerTerms);
                }

                if (keyTermsBySheet.TryGetValue(sheetId, out var keyTerms))
                {
                    terms.UnionWith(keyTerms);
                }

                if (rowOrdinateId is { } rid && termsByOrdinate.TryGetValue((sheetId, rid), out var rowTerms))
                {
                    terms.UnionWith(rowTerms);
                }

                if (termsByOrdinate.TryGetValue((sheetId, columnOrdinateId), out var colTerms))
                {
                    terms.UnionWith(colTerms);
                }

                if (terms.Count == 0)
                {
                    emptySignature++;
                    continue;
                }

                signatures.Add(new LayoutCellSignature(sheetId, sheet.TableCode, sheet.FrameworkCode, sheet.ReleaseLabel, terms.ToList()));
            }
        }

        return new PlaneCLayoutData(signatures, nonShadedExamined, emptySignature);
    }
}
