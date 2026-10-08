using EbaDpm.Converter.Core.Validation.PlaneC;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// The plane C normalization has to be SYMMETRIC -- the real failure was a regular expression that
/// matched <c>eba_met:</c> and failed on <c>eba_met_4.0:</c> (the dot and the underscore were not
/// in the character class), fabricating **249 false differences** that pointed exactly at the
/// finding being looked for.
///
/// This file pins that exact pair as a permanent regression, plus the other three cases named
/// explicitly: <c>eba_dim_4.2:qHVV</c>, <c>eba_qCS:qx2024</c> and <c>qCOE(*[new_CO1])</c>.
/// </summary>
public sealed class PlaneCSignatureNormalizationTests
{
    /// <summary>
    /// The case that broke the measurement: <c>^eba_[A-Za-z0-9]+:</c> matched <c>eba_met:</c> and
    /// failed on <c>eba_met_4.0:</c>. The class of the production normalizer includes digits, dot
    /// and underscore (<c>[A-Za-z0-9_.]+</c>) on purpose: both forms have to reduce to the SAME
    /// normalized term.
    /// </summary>
    [Fact]
    public void NormalizeTerm_EbaMet_AndEbaMet40_GiveTheSameNormalizedTerm()
    {
        var withoutReleaseSuffix = PlaneCSignature.NormalizeTerm("MET(eba_met:qKMI)");
        var withReleaseSuffix = PlaneCSignature.NormalizeTerm("MET(eba_met_4.0:qKMI)");

        Assert.Equal("MET(qKMI)", withoutReleaseSuffix);
        Assert.Equal("MET(qKMI)", withReleaseSuffix);
        Assert.Equal(withoutReleaseSuffix, withReleaseSuffix);
    }

    [Theory]
    [InlineData("eba_met:", "")]
    [InlineData("eba_met_4.0:", "")]
    [InlineData("eba_dim_4.2:qHVV", "qHVV")]
    [InlineData("eba_qCS:qx2024", "qx2024")]
    public void StripEbaPrefix_RemovesTheNamespaceAndVersionPrefix(string rawCode, string expected)
    {
        Assert.Equal(expected, PlaneCSignature.StripEbaPrefix(rawCode));
    }

    /// <summary>
    /// The open-axis bracket: the layout never writes it, so it is collapsed to a bare <c>*</c> so
    /// that it can be compared against the <c>DIM(*)</c> term produced by the open-axis key rule.
    /// </summary>
    [Fact]
    public void NormalizeTerm_OpenAxisBracket_CollapsesToAsterisk()
    {
        Assert.Equal("qCOE(*)", PlaneCSignature.NormalizeTerm("qCOE(*[new_CO1])"));
    }

    /// <summary>
    /// The exact symmetry that is required: the layout side NEVER carries the
    /// <c>eba_&lt;namespace&gt;:</c> prefix (the codes already come raw), so applying the SAME
    /// normalizer to it has to be a no-op -- and it is the reason the normalizer is applied
    /// literally the same on both sides instead of only on ours (the asymmetry is not visible when
    /// reading the code -- the code is the same on both sides; what differs is the data).
    /// </summary>
    [Theory]
    [InlineData("qXX")]
    [InlineData("qHVV")]
    [InlineData("*")]
    public void StripEbaPrefix_OnALayoutCodeWithoutPrefix_IsANoOp(string layoutCode)
    {
        Assert.Equal(layoutCode, PlaneCSignature.StripEbaPrefix(layoutCode));
    }

    [Fact]
    public void ComposeAndNormalizeTerm_BuildsTheSameAsNormalizeTerm()
    {
        var composed = PlaneCSignature.ComposeAndNormalizeTerm("eba_dim_4.2:qHVV", "eba_qCS:qx2024");
        var equivalent = PlaneCSignature.NormalizeTerm("eba_dim_4.2:qHVV(eba_qCS:qx2024)");

        Assert.Equal("qHVV(qx2024)", composed);
        Assert.Equal(equivalent, composed);
    }

    [Fact]
    public void SplitAndNormalize_SplitsAndNormalizesEachTerm()
    {
        var terms = PlaneCSignature.SplitAndNormalize("MET(eba_met_4.0:qKMI)|eba_dim_4.2:qHVV(qx2001)|qCOE(*[new_CO1])");

        Assert.Equal(["MET(qKMI)", "qHVV(qx2001)", "qCOE(*)"], terms);
    }

    [Fact]
    public void DimensionOf_ReturnsTheTokenBeforeTheParenthesis()
    {
        Assert.Equal("MET", PlaneCSignature.DimensionOf("MET(qKMI)"));
        Assert.Equal("qHVV", PlaneCSignature.DimensionOf("qHVV(qx2001)"));
    }

    /// <summary>
    /// The canonical form does NOT put the metric first (on purpose, unlike
    /// <c>Dpm20AxisAndCellLoader.ComposeCellSignature</c>): pure ordinal order by the complete
    /// term, as observed in the known-divergence census (e.g. <c>L_06.00</c>:
    /// <c>FIH(*)|MET(ei1404)|...</c> -- the F goes before the M).
    /// </summary>
    [Fact]
    public void Canonicalize_SortsOrdinallyWithoutPuttingTheMetricFirst()
    {
        var canonical = PlaneCSignature.Canonicalize(["MET(ei1404)", "FIH(*)", "si1464(*)", "ei1405(*)"]);

        Assert.Equal("FIH(*)|MET(ei1404)|ei1405(*)|si1464(*)", canonical);
    }

    [Fact]
    public void Canonicalize_IsInsensitiveToTheInputOrder()
    {
        var a = PlaneCSignature.Canonicalize(["MET(qAA)", "DIM(m1)"]);
        var b = PlaneCSignature.Canonicalize(["DIM(m1)", "MET(qAA)"]);

        Assert.Equal(a, b);
    }
}
