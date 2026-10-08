namespace EbaDpm.Converter.Core.Layouts;

/// <summary>
/// Normalises a table CODE to the canonical form used by the target schema (underscore:
/// <c>"C_01.00"</c>). The 3.2-era Annotated Table Layouts write the code with a SPACE
/// (<c>"C 01.00"</c>); since 4.0 they already write it with an underscore, like the target.
///
/// Applied ONLY on the layout side, this would be an ASYMMETRIC normaliser that fabricates
/// differences: the code is not what differs between the two sides of a comparison, it is when it
/// gets called. That is why this function is invoked EXPLICITLY on both sides of every comparison
/// by <c>TableCode</c>
/// (<see cref="EbaDpm.Converter.Core.Validation.PlaneC.PlaneCComparer"/>,
/// <see cref="EbaDpm.Converter.Core.Validation.PlaneC.PlaneCEqualityChecker"/>) — even though on
/// the generated side it is a no-op today (no codes with a space in the DPM 1.0 output). That it
/// is a no-op must be visible in the code, not deduced.
///
/// It is also applied at EXTRACTION time — <c>LayoutSheet.TableCode</c>
/// (<see cref="LayoutSheetNameParser"/>) and <c>LayoutEqualityRule.TableCode</c>
/// (<see cref="LayoutEqualityRuleParser"/>) — so that the layout repository stores the code
/// already in canonical form. The raw source literal is NOT lost: <c>LayoutSheet.SheetName</c>
/// keeps the sheet name exactly as the EBA wrote it, space included.
/// </summary>
internal static class LayoutTableCodeNormalizer
{
    public static string Canonicalize(string tableCode) => tableCode.Replace(' ', '_');
}
