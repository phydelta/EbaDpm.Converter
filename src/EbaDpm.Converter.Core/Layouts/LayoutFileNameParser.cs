using System.Text.RegularExpressions;

namespace EbaDpm.Converter.Core.Layouts;

/// <summary>The three fields that <c>LayoutFile</c> derives from the file name. <c>ModuleCode</c>
/// is <see langword="null"/> when the name does not carry it — 3.2 is per FRAMEWORK, not per
/// module: that granularity does not exist in that era and is not invented.</summary>
internal sealed record LayoutFileNameInfo(string FrameworkCode, string ReleaseLabel, string? ModuleCode);

/// <summary>
/// Parses the file name of an Annotated Table Layout. There are FOUR different formats
/// depending on the publication era. Three are resolvable in code, with no external dictionary or
/// hard-wired list of frameworks; the 4.4 one does not carry the framework and needs the dictionary.
///
/// <list type="bullet">
/// <item><b>4.2 / 4.3</b> — <c>"{date} Annotated Table Layout  {FW} {REL} {MODULE}{FW} {REL}"</c>:
/// the file repeats framework+release at the start (with a space) and at the end (stuck to the
/// module, without a space). <c>20260106 Annotated Table Layout  COREP 4.2 COREP_ALMCOREP 4.2.xlsx</c> →
/// FW=COREP, REL=4.2, MODULE=COREP_ALM.</item>
/// <item><b>4.0</b> — <c>"{date} Annotated Table Layout  {MODULE}{FW} {REL}"</c>: does NOT carry
/// the leading "{FW} {REL}", only the trailing one. <c>20241217 Annotated Table Layout  COREP_LRCOREP
/// 4.0.xlsx</c> → FW=COREP, REL=4.0, MODULE=COREP_LR. The framework is separated from the module by
/// STRUCTURE (the module starts with the framework, or the whole block is the framework repeated
/// twice when there is no submodule — <c>DORADORA</c> → FW=MODULE=DORA), never by a list of
/// known codes.</item>
/// <item><b>3.2</b> — <c>"Annotated Table Layout {PHASE}-{FW} {REL}"</c>: WITHOUT date and WITHOUT module.
/// <c>Annotated Table Layout 320-P1-COREP 3.2.xlsx</c> → FW=COREP, REL=3.2, MODULE=null. The
/// file is per complete framework (publication phase, not module): the module granularity
/// that 4.2 brings does not exist yet.</item>
/// <item><b>4.4</b> — <c>"{date} {MODULE}_table layout {REL}"</c>: NO framework in the name.
/// <c>20261007 AML_ELIGIBILITY_table layout 4.4.xlsx</c> → MODULE=AML_ELIGIBILITY, REL=4.4. The module
/// code may contain "_", so the name is anchored on the literal <c>_table layout </c>. The framework is
/// resolved from the module code through the SQLite dictionary (<see cref="LayoutDictionary"/>):
/// without a dictionary, or with a module that is not in it, the parse fails; it never guesses.</item>
/// </list>
/// </summary>
internal static class LayoutFileNameParser
{
    // Structure common to the three formats: optional date (only 4.0/4.2/4.3), fixed literal, and
    // two tokens without spaces (A, B). A third part (tail) is OPTIONAL and is what distinguishes
    // 4.2/4.3 (which carry it) from 4.0 and 3.2 (which do not — those differ from each other by
    // the date).
    private static readonly Regex Pattern = new(
        @"^(?<date>\d+\s+)?Annotated Table Layout\s+(?<a>\S+)\s+(?<b>\S+)(?:\s+(?<tail>.+))?$",
        RegexOptions.Compiled);

    // 3.2: "320-P1-COREP" / "321-P2-MREL_DECISIONS" → phase "320-P1"/"321-P2", framework the rest
    // (may contain "_", e.g. MREL_DECISIONS, NOTIF_IMPRACTICABILITY).
    private static readonly Regex PhaseAndFrameworkPattern = new(
        @"^\d+-P\d+-(?<fw>.+)$", RegexOptions.Compiled);

    // 4.4: "{date} {MODULE}_table layout {REL}". Anchored on the literal "_table layout " because the
    // module code may contain "_" (AML_ELIGIBILITY, MREL_DECISIONS).
    private static readonly Regex ModuleTableLayoutPattern = new(
        @"^(?<date>\d+)\s+(?<module>\S+)_table layout (?<rel>\S+)$", RegexOptions.Compiled);

    /// <summary>
    /// Parses the name. <paramref name="dictionary"/> is only consulted by the 4.4 format, to resolve
    /// the framework from the module code.
    /// </summary>
    public static LayoutFileNameInfo Parse(string fileNameWithoutExtension, LayoutDictionary? dictionary = null)
    {
        var moduleLayoutMatch = ModuleTableLayoutPattern.Match(fileNameWithoutExtension);
        if (moduleLayoutMatch.Success)
        {
            var moduleCode = moduleLayoutMatch.Groups["module"].Value;
            if (dictionary is null || !dictionary.IsPresent)
            {
                throw new InvalidOperationException(
                    $"The file name '{fileNameWithoutExtension}' follows the 4.4 pattern " +
                    "'<date> <MODULE>_table layout <REL>', which does not carry the framework: it is resolved from " +
                    $"the module code '{moduleCode}' through the SQLite dictionary, and no dictionary was given " +
                    "(--dictionary, the file of the layouts' release).");
            }

            if (!dictionary.TryResolveFramework(moduleCode, out var framework))
            {
                throw new InvalidOperationException(
                    $"The file '{fileNameWithoutExtension}' names the module '{moduleCode}', which is not in the " +
                    $"dictionary '{dictionary.Path}' (mModule.ModuleCode, case-insensitive): its framework cannot be " +
                    "resolved and is not guessed. --dictionary must be the file of the layouts' release.");
            }

            return new LayoutFileNameInfo(framework, moduleLayoutMatch.Groups["rel"].Value, moduleCode);
        }

        var match = Pattern.Match(fileNameWithoutExtension);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"The file name '{fileNameWithoutExtension}' does not follow any of the four " +
                "known Annotated Table Layout patterns: " +
                "'<date> Annotated Table Layout  <FW> <REL> <MODULE><FW> <REL>' (4.2/4.3), " +
                "'<date> Annotated Table Layout  <MODULE><FW> <REL>' (4.0), " +
                "'Annotated Table Layout <PHASE>-<FW> <REL>' (3.2, no date or module), or " +
                "'<date> <MODULE>_table layout <REL>' (4.4, framework resolved from the dictionary).");
        }

        var hasDate = match.Groups["date"].Success;
        var a = match.Groups["a"].Value;
        var b = match.Groups["b"].Value;
        var tail = match.Groups["tail"].Success ? match.Groups["tail"].Value : null;

        if (tail is not null)
        {
            // 4.2 / 4.3 form.
            var framework = a;
            var release = b;
            var suffix = framework + " " + release;

            if (!tail.EndsWith(suffix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The file name '{fileNameWithoutExtension}' does not repeat " +
                    $"'{suffix}' at the end, as the expected pattern requires.");
            }

            var module = tail[..^suffix.Length];
            return new LayoutFileNameInfo(framework, release, module);
        }

        if (hasDate)
        {
            // 4.0 form: "<MODULE><FW> <REL>" — without the leading "<FW> <REL>" that 4.2/4.3 carry.
            var (moduleCode, frameworkCode) = SplitModuleAndFramework(a, fileNameWithoutExtension);
            return new LayoutFileNameInfo(frameworkCode, b, moduleCode);
        }

        // 3.2 form: no date, no module — "<PHASE>-<FW> <REL>".
        var phaseMatch = PhaseAndFrameworkPattern.Match(a);
        if (!phaseMatch.Success)
        {
            throw new InvalidOperationException(
                $"The file name '{fileNameWithoutExtension}' has no date (it looks like the " +
                $"3.2 era), but '{a}' is not shaped like '<phase>-<framework>'.");
        }

        return new LayoutFileNameInfo(phaseMatch.Groups["fw"].Value, b, null);
    }

    /// <summary>
    /// Splits <c>"{MODULE}{FW}"</c> (4.0 format, no space between the two parts) by
    /// STRUCTURE: the module, when it exists as a submodule, starts with the framework followed by
    /// "_" (<c>"COREP_LR"</c> + <c>"COREP"</c>); when the module IS the framework (no
    /// submodule, e.g. <c>DORA</c>) the whole block is the framework repeated twice in a row
    /// (<c>"DORADORA"</c>). Neither branch needs to know in advance which framework codes
    /// exist.
    /// </summary>
    private static (string ModuleCode, string FrameworkCode) SplitModuleAndFramework(
        string blob, string fileNameWithoutExtension)
    {
        var underscoreIndex = blob.IndexOf('_', StringComparison.Ordinal);
        if (underscoreIndex >= 0)
        {
            var candidateFramework = blob[..underscoreIndex];
            if (blob.EndsWith(candidateFramework, StringComparison.Ordinal))
            {
                return (blob[..^candidateFramework.Length], candidateFramework);
            }
        }

        if (blob.Length % 2 == 0)
        {
            var half = blob.Length / 2;
            var firstHalf = blob[..half];
            var secondHalf = blob[half..];
            if (firstHalf.Length > 0 && string.Equals(firstHalf, secondHalf, StringComparison.Ordinal))
            {
                return (firstHalf, firstHalf);
            }
        }

        throw new InvalidOperationException(
            $"The file name '{fileNameWithoutExtension}' looks like the 4.0 era, but " +
            $"'{blob}' cannot be split into module+framework by either of the two known forms.");
    }
}
