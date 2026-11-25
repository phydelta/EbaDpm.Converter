using EbaDpm.Converter.Native.PlaneC;

namespace EbaDpm.Converter.Native.Tests.PlaneC;

/// <summary>
/// The native signature of a cell (docs/validation.md, "Native output", "Plane C"):
/// <c>MET(&lt;metric code&gt;)</c> plus <c>&lt;property&gt;(&lt;item&gt;)</c> for each context pair whose item is not
/// the default item at the release R, plus <c>&lt;property&gt;(*)</c> for each key header; the codes are those at
/// R; an excluded cell has none. Each rule is checked with a case that differs from it by one fact, so a
/// signature builder that ignored the rule would give the other answer.
/// </summary>
public sealed class NativeSignatureTests
{
    private const long R42 = 1;
    private const long R43 = 2;
    private const long R44 = 3;

    private static (NativeSignatureBuilder Builder, NativeTableData Table, NativeStructure Structure) Load(
        SyntheticNativeFile native, long tableVid, long releaseId)
    {
        var structure = NativeStructure.Load(native.Connection, new HashSet<long> { tableVid });
        var builder = new NativeSignatureBuilder(structure, NativeItemCodes.Load(native.Connection), releaseId);
        return (builder, structure.Tables[tableVid], structure);
    }

    private static string[] Terms(NativeSignatureBuilder builder, NativeTableData table, long cellId)
    {
        var cell = table.Cells.Single(c => c.CellId == cellId);
        var (signature, status) = builder.SignatureOf(table, cell);
        Assert.Equal(NativeSignatureBuilder.CellStatus.Ok, status);
        return signature!.Terms.Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void Signature_IsTheMetricAndTheContextPairs()
    {
        using var native = new SyntheticNativeFile();
        var metric = native.Property("qAAA");
        var dimension = native.Property("qDIM");
        var member = native.Member("MEM", "qM1");
        var table = native.TableVersion("T_01.00");
        var cell = native.Cell(table, native.Variable("fact", metric, (dimension, member)));

        var (builder, data, _) = Load(native, table, R44);

        Assert.Equal(["MET(qAAA)", "qDIM(qM1)"], Terms(builder, data, cell));
        var (signature, _) = builder.SignatureOf(data, data.Cells.Single());
        Assert.Equal("MET(qAAA)|qDIM(qM1)", signature!.Canonical);
    }

    [Fact]
    public void ContextPair_WithTheDefaultItem_IsDropped_AndWithAnotherItemIsKept()
    {
        using var native = new SyntheticNativeFile();
        var metric = native.Property("qAAA");
        var dimension = native.Property("qDIM");
        var other = native.Property("qOTH");
        var defaultMember = native.Member("MEM", "qDEF", isDefault: true);
        var plainMember = native.Member("MEM", "qM1");
        var table = native.TableVersion("T_01.00");
        var withDefault = native.Cell(table, native.Variable("fact", metric, (dimension, defaultMember)));
        var withPlain = native.Cell(table, native.Variable("fact", metric, (dimension, plainMember), (other, defaultMember)));

        var (builder, data, _) = Load(native, table, R44);

        Assert.Equal(["MET(qAAA)"], Terms(builder, data, withDefault));
        Assert.Equal(["MET(qAAA)", "qDIM(qM1)"], Terms(builder, data, withPlain));
    }

    [Fact]
    public void Metric_IsNeverDropped_EvenWhenItsItemIsMarkedDefault()
    {
        using var native = new SyntheticNativeFile();
        var metric = native.Property("qAAA", isDefault: true);
        var table = native.TableVersion("T_01.00");
        var cell = native.Cell(table, native.Variable("fact", metric));

        var (builder, data, _) = Load(native, table, R44);

        Assert.Equal(["MET(qAAA)"], Terms(builder, data, cell));
    }

    [Fact]
    public void KeyHeader_IsAddedAsPropertyStar_AndNonKeyHeaderIsNot()
    {
        using var native = new SyntheticNativeFile();
        var metric = native.Property("qAAA");
        var keyProperty = native.Property("qKEY");
        var columnProperty = native.Property("qCOL");
        var table = native.TableVersion("T_01.00");
        native.Header(table, "Y", isKey: true, "0010", keyProperty);
        native.Header(table, "X", isKey: false, "0020", columnProperty);
        var cell = native.Cell(table, native.Variable("fact", metric));

        var (builder, data, _) = Load(native, table, R44);

        Assert.Equal(["MET(qAAA)", "qKEY(*)"], Terms(builder, data, cell));
    }

    [Fact]
    public void Codes_AreTakenAtTheReleaseOfTheComparison()
    {
        using var native = new SyntheticNativeFile();
        var metric = native.Property("qAAA");
        var dimension = native.Property("qDIM");
        var member = native.Member("MEM", "qOLD", start: R42, end: R43);
        native.Item("MEM", "qNEW", start: R43, itemId: member); // the code of the same item changes at 4.3.
        var table = native.TableVersion("T_01.00");
        var cell = native.Cell(table, native.Variable("fact", metric, (dimension, member)));

        var (at42, data42, _) = Load(native, table, R42);
        var (at43, data43, _) = Load(native, table, R43);
        var (at44, data44, _) = Load(native, table, R44);

        Assert.Equal(["MET(qAAA)", "qDIM(qOLD)"], Terms(at42, data42, cell));
        Assert.Equal(["MET(qAAA)", "qDIM(qNEW)"], Terms(at43, data43, cell));
        Assert.Equal(["MET(qAAA)", "qDIM(qNEW)"], Terms(at44, data44, cell));
    }

    [Fact]
    public void DefaultFlag_IsTheOneOfTheRowValidAtTheRelease()
    {
        using var native = new SyntheticNativeFile();
        var metric = native.Property("qAAA");
        var dimension = native.Property("qDIM");
        var member = native.Member("MEM", "qM1", start: R42, end: R43, isDefault: true);
        native.Item("MEM", "qM1", start: R43, isDefault: false, itemId: member); // no longer the default from 4.3.
        var table = native.TableVersion("T_01.00");
        var cell = native.Cell(table, native.Variable("fact", metric, (dimension, member)));

        var (at42, data42, _) = Load(native, table, R42);
        var (at43, data43, _) = Load(native, table, R43);

        Assert.Equal(["MET(qAAA)"], Terms(at42, data42, cell));
        Assert.Equal(["MET(qAAA)", "qDIM(qM1)"], Terms(at43, data43, cell));
    }

    [Fact]
    public void ExcludedCell_HasNoSignature_AndANonExcludedCellOfTheSameVariableHasOne()
    {
        using var native = new SyntheticNativeFile();
        var metric = native.Property("qAAA");
        var table = native.TableVersion("T_01.00");
        var variable = native.Variable("fact", metric);
        var excluded = native.Cell(table, variable, excluded: true);
        var included = native.Cell(table, variable);

        var (builder, data, _) = Load(native, table, R44);

        var (signature, status) = builder.SignatureOf(data, data.Cells.Single(c => c.CellId == excluded));
        Assert.Null(signature);
        Assert.Equal(NativeSignatureBuilder.CellStatus.Excluded, status);
        Assert.Equal(["MET(qAAA)"], Terms(builder, data, included));

        var (signatures, missing, withoutVariable) = builder.Build(data);
        Assert.Single(signatures);
        Assert.Equal(0, missing);
        Assert.Equal(0, withoutVariable);
    }

    [Fact]
    public void CellWithoutVariable_HasNoSignature_AndIsCounted()
    {
        using var native = new SyntheticNativeFile();
        var table = native.TableVersion("T_01.00");
        native.Cell(table, variableVid: null);

        var (builder, data, _) = Load(native, table, R44);

        var (signatures, missing, withoutVariable) = builder.Build(data);
        Assert.Empty(signatures);
        Assert.Equal(0, missing);
        Assert.Equal(1, withoutVariable);
    }

    [Fact]
    public void CodeMissingAtTheRelease_GivesNoSignature_ItIsReported_AndNothingIsGuessed()
    {
        using var native = new SyntheticNativeFile();
        var metric = native.Property("qAAA");
        var dimension = native.Property("qDIM");
        var member = native.Member("MEM", "qM1", start: R43); // does not exist at 4.2.
        var table = native.TableVersion("T_01.00");
        var cell = native.Cell(table, native.Variable("fact", metric, (dimension, member)));

        var (at42, data42, _) = Load(native, table, R42);
        var (signature, status) = at42.SignatureOf(data42, data42.Cells.Single());

        Assert.Null(signature);
        Assert.Equal(NativeSignatureBuilder.CellStatus.MissingCode, status);
        Assert.Equal(1, at42.MissingByKind["context item"]);
        Assert.Contains(at42.MissingSamples, s => s.StartsWith("context item ID=", StringComparison.Ordinal));
        var (signatures, missing, withoutVariable) = at42.Build(data42);
        Assert.Equal((0, 1L, 0L), (signatures.Count, missing, withoutVariable));

        // The same variable at a release where the code exists has its signature.
        var (at43, data43, _) = Load(native, table, R43);
        Assert.Equal(["MET(qAAA)", "qDIM(qM1)"], Terms(at43, data43, cell));
    }

    [Fact]
    public void TwoRowsValidAtTheRelease_AreAmbiguous_AndGiveNoSignature()
    {
        using var native = new SyntheticNativeFile();
        var metric = native.Property("qAAA");
        var dimension = native.Property("qDIM");
        var member = native.Member("MEM", "qM1");
        native.Item("MEM", "qM2", start: R43, itemId: member); // a second row of the same item, also valid at 4.4.
        var table = native.TableVersion("T_01.00");
        native.Cell(table, native.Variable("fact", metric, (dimension, member)));

        var (builder, data, _) = Load(native, table, R44);
        var (signature, status) = builder.SignatureOf(data, data.Cells.Single());

        Assert.Null(signature);
        Assert.Equal(NativeSignatureBuilder.CellStatus.MissingCode, status);
        Assert.Contains("context item (several rows valid)", builder.MissingByKind.Keys);
    }

    [Fact]
    public void EbaPrefix_IsStrippedFromCodes_AsOnTheLayoutSide()
    {
        using var native = new SyntheticNativeFile();
        var metric = native.Property("eba_met:qAAA");
        var dimension = native.Property("eba_dim:qDIM");
        var member = native.Member("MEM", "eba_MC:x1");
        var table = native.TableVersion("T_01.00");
        var cell = native.Cell(table, native.Variable("fact", metric, (dimension, member)));

        var (builder, data, _) = Load(native, table, R44);

        Assert.Equal(["MET(qAAA)", "qDIM(x1)"], Terms(builder, data, cell));
    }
}
