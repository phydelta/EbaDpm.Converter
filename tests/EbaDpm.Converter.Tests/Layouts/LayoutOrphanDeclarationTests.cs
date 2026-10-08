namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// <c>LayoutUnparsed</c> mixes several distinct directions ("values without a declaration" --
/// orphans -- and "declarations without a value", which do NOT really belong there). This test
/// measures the directions over the real 4.2 data:
///
/// <list type="bullet">
/// <item><b>Values without a declaration</b> (real orphans): 0 over the 51 real files.</item>
/// <item><b>Declarations without any associated value</b>: 693 of 11,515. Of these, 426 are
/// <c>IsKey = 1</c> (correct by design: an open axis declares the key without fixing a value).
/// Of the remaining 267 (<c>IsKey = 0</c>), 251 follow the benign "open-axis key column" pattern
/// (they have an <c>IsKey = 1</c> sibling in the same sheet for the same <c>DimensionCode</c> --
/// e.g. PBE in <c>C_106.00</c>, qSIC in <c>C_14.01</c>). The remaining 16 are
/// <c>qBGD</c>/<c>qEBB</c>/<c>qBRK</c>/<c>qMRW</c> in the 6 sheets of <c>C_14.01</c>, verified
/// against the real signature (they carry no term in any real signature of the reference) -- this
/// is not a loss either, the EBA layout simply does not fix that dimension there.</item>
/// </list>
///
/// Conclusion: reusing the table does NOT lose information -- the 693 "declarations without a
/// value" REMAIN recorded in <c>LayoutDeclaration</c> -- and the directions are told apart with
/// <c>LayoutUnparsed.Kind</c>, so separating them needs no forensic query: <c>GrammarFailure</c>,
/// <c>OrphanValue</c>, <c>OrphanDeclaration</c>, <c>UnlinkedValue</c>.
///
/// The global <c>LayoutUnparsed</c> total is 693 + 238 = 931: the 693 orphan declarations of this
/// file plus the 238 <c>UnlinkedValue</c> rows (see <see cref="LayoutValueOrdinateLinkingTests"/>),
/// with <b>0</b> rows of the other two directions (<c>GrammarFailure</c>, <c>OrphanValue</c>)
/// because the 51 real files, verified by hand and against the real signature of the reference
/// (see <see cref="LayoutZSuffixMissingHeaderTests"/>), contain neither broken syntax nor invented
/// codes -- only the POSITIVE control of <see cref="LayoutUnparsedGateTests"/> shows that those
/// zeros mean something. The test checks the explicit sum, not a direct equality against the table
/// total.
/// </summary>
[Collection("LayoutsRealData")]
public sealed class LayoutOrphanDeclarationTests(LayoutRepositoryFixture fixture)
{
    [DataFact]
    public void RealData_LayoutUnparsed_Is693OrphanDeclaration_Plus238UnlinkedValue_ZeroGrammarFailure_ZeroOrphanValue()
    {
        // The total is 693 + 238 = 931. The two figures are checked separately, each with its own
        // Kind, instead of a single count equality -- so a regression in either direction remains
        // detectable. UnlinkedValue: no LayoutValue that fails to link to its ordinate is silently
        // discarded (see LayoutValueOrdinateLinker).
        var con = fixture.Repository42WithDictionary;

        using var kindCmd = con.CreateCommand();
        kindCmd.CommandText = "SELECT Kind, COUNT(*) FROM LayoutUnparsed GROUP BY Kind";
        using var reader = kindCmd.ExecuteReader();
        var byKind = new Dictionary<string, long>();
        while (reader.Read())
        {
            byKind[reader.GetString(0)] = reader.GetInt64(1);
        }

        Assert.Equal(693L, byKind.GetValueOrDefault("OrphanDeclaration"));
        Assert.Equal(238L, byKind.GetValueOrDefault("UnlinkedValue"));
        Assert.False(byKind.ContainsKey("GrammarFailure"), "0 expected, not a silent absence: if it appears, there is real broken syntax in 4.2 that has not been characterised.");
        Assert.False(byKind.ContainsKey("OrphanValue"), "0 expected: if it appears, there is an invented or real orphan code in 4.2 that has not been characterised.");

        using var totalCmd = con.CreateCommand();
        totalCmd.CommandText = "SELECT COUNT(*) FROM LayoutUnparsed";
        var total = Convert.ToInt64(totalCmd.ExecuteScalar());
        Assert.Equal(byKind.Values.Sum(), total);
        Assert.Equal(931L, total);
    }

    [DataFact]
    public void RealData_693DeclarationsWithoutAnyValue_BreakDownExactlyAsCharacterised()
    {
        var con = fixture.Repository42WithDictionary;

        using var totalCmd = con.CreateCommand();
        totalCmd.CommandText = "SELECT COUNT(*) FROM LayoutDeclaration";
        Assert.Equal(11515L, Convert.ToInt64(totalCmd.ExecuteScalar()));

        using var orphanCmd = con.CreateCommand();
        orphanCmd.CommandText =
            """
            SELECT COUNT(*) FROM LayoutDeclaration d
            WHERE NOT EXISTS (SELECT 1 FROM LayoutValue v WHERE v.DeclId = d.DeclId)
            """;
        Assert.Equal(693L, Convert.ToInt64(orphanCmd.ExecuteScalar()));

        using var isKeyCmd = con.CreateCommand();
        isKeyCmd.CommandText =
            """
            SELECT COUNT(*) FROM LayoutDeclaration d
            WHERE d.IsKey = 1 AND NOT EXISTS (SELECT 1 FROM LayoutValue v WHERE v.DeclId = d.DeclId)
            """;
        Assert.Equal(426L, Convert.ToInt64(isKeyCmd.ExecuteScalar()));

        // Of the remaining 267 (IsKey = 0), 251 have an IsKey=1 sibling with the same
        // DimensionCode in the same sheet (the benign "key column" pattern).
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            SELECT d.DeclId, d.SheetId, d.DimensionCode
            FROM LayoutDeclaration d
            WHERE d.IsKey = 0 AND NOT EXISTS (SELECT 1 FROM LayoutValue v WHERE v.DeclId = d.DeclId)
            """;
        using var reader = cmd.ExecuteReader();
        var nonKeyOrphans = new List<(long DeclId, long SheetId, string? DimensionCode)>();
        while (reader.Read())
        {
            nonKeyOrphans.Add((reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        Assert.Equal(267, nonKeyOrphans.Count);

        var withKeySibling = 0;
        var withoutSibling = new List<(long DeclId, long SheetId, string? DimensionCode)>();
        foreach (var orphan in nonKeyOrphans)
        {
            if (orphan.DimensionCode is null)
            {
                withoutSibling.Add(orphan);
                continue;
            }

            using var siblingCmd = con.CreateCommand();
            siblingCmd.CommandText =
                "SELECT COUNT(*) FROM LayoutDeclaration WHERE SheetId = $sid AND DimensionCode = $dim AND IsKey = 1";
            siblingCmd.Parameters.AddWithValue("$sid", orphan.SheetId);
            siblingCmd.Parameters.AddWithValue("$dim", orphan.DimensionCode);
            if (Convert.ToInt64(siblingCmd.ExecuteScalar()) > 0)
            {
                withKeySibling++;
            }
            else
            {
                withoutSibling.Add(orphan);
            }
        }

        Assert.Equal(251, withKeySibling);
        Assert.Equal(16, withoutSibling.Count);

        // The remaining 16 all belong to the same group of tables (C_14.01, verified against the
        // real signature in LayoutOrdinateXAxisCodeTests/LayoutRealDataContrastTests): their
        // dimensions (qBGD, qEBB, qBRK, qMRW) carry no term in the real signature -- not a loss.
        var expectedDims = new HashSet<string> { "qBGD", "qEBB", "qBRK", "qMRW" };
        Assert.All(withoutSibling, o => Assert.Contains(o.DimensionCode!, expectedDims));
    }
}
