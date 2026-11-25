using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Native.Layouts;
using EbaDpm.Converter.Native.Tests.PlaneC;

namespace EbaDpm.Converter.Native.Tests.Layouts;

/// <summary>
/// The layout dictionary built from a native output (docs/table-layouts.md, "Dictionary from a native
/// output"): metrics, dimensions and their domains, (domain, member) pairs and module -> framework,
/// each from a synthetic native file (the real native schema, a few rows). Every rule has a control
/// next to it that a loader ignoring the rule would answer differently.
/// </summary>
public sealed class NativeLayoutDictionaryLoaderTests
{
    private static LayoutDictionary Load(SyntheticNativeFile native) => NativeLayoutDictionaryLoader.Load(native.FilePath);

    private static SingleCodeResolution Resolve(LayoutDictionary dictionary, string code, out string? domain) =>
        dictionary.Resolve(code, out domain);

    private static void DataType(SyntheticNativeFile native, long id, string code) =>
        native.Insert("DataType", ("DataTypeID", id), ("Code", code), ("Name", code));

    private static void PropertyRow(SyntheticNativeFile native, long propertyId, long dataTypeId) =>
        native.Exec($"UPDATE \"Property\" SET \"DataTypeID\" = {dataTypeId} WHERE \"PropertyID\" = {propertyId}");

    private static void PropertyCategory(SyntheticNativeFile native, long propertyId, string category, long start = 1, long? end = null) =>
        native.Insert(
            "PropertyCategory",
            ("PropertyID", propertyId), ("StartReleaseID", start), ("EndReleaseID", end), ("CategoryID", native.Category(category)));

    // ------------------------------------------------------------------ metrics

    [Fact]
    public void Metric_ByTheFactVariableThatUsesItsProperty()
    {
        using var native = new SyntheticNativeFile();
        var viaFact = native.Property("zFact");
        native.Property("zNoRole");
        native.Variable("fact", viaFact);

        using var dictionary = Load(native);

        Assert.Equal(SingleCodeResolution.Metric, Resolve(dictionary, "zFact", out _));
        Assert.Equal(SingleCodeResolution.Unknown, Resolve(dictionary, "zNoRole", out _));
        Assert.True(dictionary.IsValidDomainMember("MET", "zFact"));
        Assert.False(dictionary.IsValidDomainMember("MET", "zNoRole"));
    }

    [Fact]
    public void Metric_ByTheNonKeyHeaderThatUsesItsProperty_ButNotByAKeyHeader()
    {
        using var native = new SyntheticNativeFile();
        var viaHeader = native.Property("zHeader");
        var viaKeyHeader = native.Property("zKeyHeader");
        var table = native.TableVersion("T_01.00");
        native.Header(table, "X", isKey: false, "0010", viaHeader);
        native.Header(table, "Y", isKey: true, "0020", viaKeyHeader);

        using var dictionary = Load(native);

        Assert.Equal(SingleCodeResolution.Metric, Resolve(dictionary, "zHeader", out _));
        Assert.Equal(SingleCodeResolution.Unknown, Resolve(dictionary, "zKeyHeader", out _));
    }

    [Theory]
    [InlineData("ab123", true)]    // two letters and digits: the naming law of metrics.
    [InlineData("EI1404", true)]
    [InlineData("qAAA", false)]    // letters only.
    [InlineData("abc123", false)]  // three letters.
    [InlineData("a1", false)]      // one letter.
    [InlineData("ab12c", false)]   // a trailing letter.
    [InlineData("ab", false)]      // no digits.
    public void Metric_ByTheNamingConventionOfItsOwnCode(string code, bool isMetric)
    {
        using var native = new SyntheticNativeFile();
        native.Property(code);

        using var dictionary = Load(native);

        Assert.Equal(isMetric ? SingleCodeResolution.Metric : SingleCodeResolution.Unknown, Resolve(dictionary, code, out _));
        Assert.Equal(isMetric, dictionary.IsValidDomainMember("MET", code));
    }

    [Fact]
    public void Metric_CodeOfAnEarlierRelease_IsInTheDictionary_TheDictionaryCoversEveryRelease()
    {
        using var native = new SyntheticNativeFile();
        var metric = native.Property("zOldFact", start: 1, end: 2); // not valid at 4.4.
        native.Variable("fact", metric);

        using var dictionary = Load(native);

        Assert.Equal(SingleCodeResolution.Metric, Resolve(dictionary, "zOldFact", out _));
    }

    // ------------------------------------------------------------------ dimensions

    [Fact]
    public void Dimension_FromAContextCompositionProperty_WithTheCategoryOfItsPropertyCategory()
    {
        using var native = new SyntheticNativeFile();
        var metric = native.Property("zFact");
        var dimension = native.Property("qDIM");
        var member = native.Member("MEM", "qM1");
        PropertyCategory(native, dimension, "MEM");
        native.Variable("fact", metric, (dimension, member));
        native.Property("qUNUSED"); // control: a property no context uses is not a dimension.
        PropertyCategory(native, native.Property("qUNUSED2"), "MEM");

        using var dictionary = Load(native);

        Assert.Equal(SingleCodeResolution.Dimension, Resolve(dictionary, "qDIM", out var domain));
        Assert.Equal("MEM", domain);
        Assert.Equal(SingleCodeResolution.Unknown, Resolve(dictionary, "qUNUSED", out _));
        Assert.Equal(SingleCodeResolution.Unknown, Resolve(dictionary, "qUNUSED2", out _));
    }

    [Fact]
    public void Dimension_FromThePropertyOfAKeyVariable()
    {
        using var native = new SyntheticNativeFile();
        var keyProperty = native.Property("qKEYV");
        var factProperty = native.Property("qFACTONLY");
        PropertyCategory(native, keyProperty, "MEM");
        PropertyCategory(native, factProperty, "MEM");
        native.Variable("key", keyProperty);
        native.Variable("fact", factProperty); // a fact variable's property is a metric, not a dimension.

        using var dictionary = Load(native);

        Assert.Equal(SingleCodeResolution.Dimension, Resolve(dictionary, "qKEYV", out var domain));
        Assert.Equal("MEM", domain);
        Assert.NotEqual(SingleCodeResolution.Dimension, Resolve(dictionary, "qFACTONLY", out _));
    }

    [Fact]
    public void Dimension_NaDomain_IsTypedByTheDataTypeOfTheProperty()
    {
        using var native = new SyntheticNativeFile();
        DataType(native, 1, "i");
        DataType(native, 2, "s");
        DataType(native, 3, "b");
        var integer = native.Property("qINT");
        var text = native.Property("qSTR");
        var other = native.Property("qOTH");
        PropertyRow(native, integer, 1);
        PropertyRow(native, text, 2);
        PropertyRow(native, other, 3);
        foreach (var property in new[] { integer, text, other })
        {
            PropertyCategory(native, property, "_NA");
        }

        var member = native.Member("MEM", "qM1");
        native.Variable("fact", native.Property("zFact"), (integer, member), (text, member), (other, member));

        using var dictionary = Load(native);

        Assert.Equal(SingleCodeResolution.Dimension, Resolve(dictionary, "qINT", out var intDomain));
        Assert.Equal("INT_NA", intDomain);
        Assert.Equal(SingleCodeResolution.Dimension, Resolve(dictionary, "qSTR", out var strDomain));
        Assert.Equal("STR_NA", strDomain);
        Assert.Equal(SingleCodeResolution.Dimension, Resolve(dictionary, "qOTH", out var otherDomain));
        Assert.Null(otherDomain); // another data type has no typed domain.
    }

    [Fact]
    public void Dimension_MetricDimensionMet_IsAlwaysPresent_EvenInAnEmptyFile()
    {
        using var native = new SyntheticNativeFile();

        using var dictionary = Load(native);

        Assert.Equal(SingleCodeResolution.Dimension, Resolve(dictionary, "MET", out var domain));
        Assert.Equal("MET", domain);
        Assert.Equal(SingleCodeResolution.Unknown, Resolve(dictionary, "NOTMET", out _));
    }

    [Theory]
    [InlineData("_TE")]
    [InlineData("SE")]
    public void Dimension_PropertyWhoseValuesLiveInAStructuralCategory_IsNotADimension(string structural)
    {
        using var native = new SyntheticNativeFile();
        var template = native.Property("qTEM");
        var real = native.Property("qREAL");
        var member = native.Member("MEM", "qM1");
        PropertyCategory(native, template, structural);
        PropertyCategory(native, real, "MEM");
        native.Variable("fact", native.Property("zFact"), (template, member), (real, member));

        using var dictionary = Load(native);

        Assert.Equal(SingleCodeResolution.Unknown, Resolve(dictionary, "qTEM", out _));
        Assert.Equal(SingleCodeResolution.Dimension, Resolve(dictionary, "qREAL", out _)); // the same file, a real domain.
    }

    [Fact]
    public void Dimension_Domain_IsTheCategoryWhoseWindowOverlapsTheWindowOfTheCode_OrTheFirstWhenNoneDoes()
    {
        using var native = new SyntheticNativeFile();
        // One property whose code changes at 4.3, and whose category changes at 4.3 too.
        var property = native.Property("qOLDDIM", start: 1, end: 2);
        native.Item("_PR", "qNEWDIM", start: 2, itemId: property);
        PropertyCategory(native, property, "CA1", start: 1, end: 2);
        PropertyCategory(native, property, "CA2", start: 2);
        // A property whose only category window does not overlap the window of its code: the first one is taken.
        var lonely = native.Property("qLONELY", start: 1, end: 2);
        PropertyCategory(native, lonely, "CA3", start: 5, end: 6);
        var member = native.Member("MEM", "qM1");
        native.Variable("fact", native.Property("zFact"), (property, member), (lonely, member));

        using var dictionary = Load(native);

        Assert.Equal(SingleCodeResolution.Dimension, Resolve(dictionary, "qOLDDIM", out var oldDomain));
        Assert.Equal("CA1", oldDomain);
        Assert.Equal(SingleCodeResolution.Dimension, Resolve(dictionary, "qNEWDIM", out var newDomain));
        Assert.Equal("CA2", newDomain);
        Assert.Equal(SingleCodeResolution.Dimension, Resolve(dictionary, "qLONELY", out var lonelyDomain));
        Assert.Equal("CA3", lonelyDomain);
    }

    // ------------------------------------------------------------------ (domain, member) pairs

    [Fact]
    public void DomainMemberPairs_ExcludeTheStructuralCategories_AndIncludeMetricsUnderMet()
    {
        using var native = new SyntheticNativeFile();
        native.Member("MEM", "qM1");
        native.Member("SE", "qSE1");
        native.Member("_NA", "qNA1");
        native.Member("_TE", "qTE1");
        var metric = native.Property("zFact");
        native.Variable("fact", metric);

        using var dictionary = Load(native);

        Assert.True(dictionary.IsValidDomainMember("MEM", "qM1"));
        Assert.False(dictionary.IsValidDomainMember("SE", "qSE1"));
        Assert.False(dictionary.IsValidDomainMember("_NA", "qNA1"));
        Assert.False(dictionary.IsValidDomainMember("_TE", "qTE1"));
        Assert.False(dictionary.IsValidDomainMember("_PR", "zFact")); // the properties are not members of a domain...
        Assert.True(dictionary.IsValidDomainMember("MET", "zFact")); // ...a metric is a member of MET.
        Assert.False(dictionary.IsValidDomainMember("MEM", "qSE1")); // the pair is (domain, member), not any member.
        Assert.False(dictionary.IsValidDomainMember("MET", "qM1"));
    }

    [Fact]
    public void DomainMemberPairs_CoverEveryReleaseOfTheFile()
    {
        using var native = new SyntheticNativeFile();
        native.Member("MEM", "qOLDMEM", start: 1, end: 2);

        using var dictionary = Load(native);

        Assert.True(dictionary.IsValidDomainMember("MEM", "qOLDMEM"));
    }

    // ------------------------------------------------------------------ module -> framework

    private static void Module(SyntheticNativeFile native, long moduleId, string frameworkCode, string moduleCode, bool isDocument = false)
    {
        var frameworkId = frameworkCode.GetHashCode(StringComparison.Ordinal) & 0xFFFF;
        if (native.Scalar($"SELECT COUNT(1) FROM \"Framework\" WHERE \"FrameworkID\" = {frameworkId}") == 0)
        {
            native.Insert("Framework", ("FrameworkID", (long)frameworkId), ("Code", frameworkCode), ("Name", frameworkCode));
        }

        native.Insert("Module", ("ModuleID", moduleId), ("FrameworkID", (long)frameworkId), ("isDocumentModule", isDocument ? 1L : 0L));
        native.Insert(
            "ModuleVersion",
            ("ModuleVID", moduleId * 10), ("ModuleID", moduleId), ("Code", moduleCode), ("Name", moduleCode), ("StartReleaseID", 1L));
    }

    [Fact]
    public void Framework_OfAModule_IsResolvedFromTheModuleVersionCode_CaseInsensitively()
    {
        using var native = new SyntheticNativeFile();
        Module(native, 1, "COREP", "COREP_OF");
        Module(native, 2, "FINREP", "FINREP9");

        using var dictionary = Load(native);

        Assert.True(dictionary.TryResolveFramework("COREP_OF", out var corep));
        Assert.Equal("COREP", corep);
        Assert.True(dictionary.TryResolveFramework("corep_of", out var lower));
        Assert.Equal("COREP", lower);
        Assert.True(dictionary.TryResolveFramework("FINREP9", out var finrep));
        Assert.Equal("FINREP", finrep);
        Assert.False(dictionary.TryResolveFramework("NO_SUCH_MODULE", out _));
    }

    [Fact]
    public void Framework_DocumentModules_AreExcluded()
    {
        using var native = new SyntheticNativeFile();
        Module(native, 1, "COREP", "COREP_OF");
        Module(native, 2, "DOCFW", "DOC_MODULE", isDocument: true);

        using var dictionary = Load(native);

        Assert.True(dictionary.TryResolveFramework("COREP_OF", out _));
        Assert.False(dictionary.TryResolveFramework("DOC_MODULE", out _));
    }

    [Fact]
    public void Framework_ACodeOfTwoFrameworks_IsAnError_NeverResolvedByGuessing()
    {
        using var native = new SyntheticNativeFile();
        Module(native, 1, "COREP", "SHARED");
        Module(native, 2, "FINREP", "SHARED");

        using var dictionary = Load(native);

        var exception = Assert.Throws<InvalidOperationException>(() => dictionary.TryResolveFramework("SHARED", out _));
        Assert.Contains("COREP", exception.Message);
        Assert.Contains("FINREP", exception.Message);
    }

    // ------------------------------------------------------------------ the dictionary itself

    [Fact]
    public void Dictionary_RecordsItsPath_AndIsPresent()
    {
        using var native = new SyntheticNativeFile();

        using var dictionary = Load(native);

        Assert.True(dictionary.IsPresent);
        Assert.Equal(native.FilePath, dictionary.Path);
    }
}
