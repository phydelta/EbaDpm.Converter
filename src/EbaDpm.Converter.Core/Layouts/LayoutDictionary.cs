namespace EbaDpm.Converter.Core.Layouts;

/// <summary>What a single-term code "(A)" is, according to what really exists in the
/// dictionary (it is NOT decided by the shape of the text).</summary>
public enum SingleCodeResolution
{
    /// <summary>Not a real DPM code (neither a member of <c>MET</c> nor a dimension): a rhetorical
    /// parenthesis such as "(a)", "(EU)", "(call)" — it is not an annotation, and it is not recorded
    /// in <c>LayoutUnparsed</c>.</summary>
    Unknown,

    /// <summary>Member of the <c>MET</c> domain: the metric of the <c>Main Property</c> column.</summary>
    Metric,

    /// <summary>Code of <c>mDimension</c>: the typical dimension of the key column of an open axis
    /// (case <c>C_106.00</c> <c>D8=(PBE)</c>).</summary>
    Dimension,
}

/// <summary>
/// Dictionary of DPM codes (metric members, dimension codes, (domain, member) pairs and the
/// framework of each module) against which the single-code ambiguity is resolved. It only HOLDS
/// the data: a loader fills it from a concrete source (for the 45-table distribution schema, a
/// reference database or the converter's own generated output, see the distribution project) —
/// this is a MECHANICAL query of which codes exist, not an arbitration of content.
///
/// Without a dictionary (<paramref name="path"/> null), every single-term code resolves to
/// <see cref="SingleCodeResolution.Unknown"/>: the caller decides the conservative default
/// (treat it as a metric, with a warning) — see <c>LayoutSheetParser</c>.
/// </summary>
public sealed class LayoutDictionary : IDisposable
{
    private readonly HashSet<string> _metMembers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _dimensionDomainCode = new(StringComparer.Ordinal);
    private readonly HashSet<(string DomainCode, string MemberCode)> _domainMemberPairs = new();

    private readonly Func<Dictionary<string, HashSet<string>>>? _frameworksByModuleCodeLoader;

    private Dictionary<string, HashSet<string>>? _frameworksByModuleCode;

    public bool IsPresent { get; }

    public string? Path { get; }

    public LayoutDictionary(string? path, Func<Dictionary<string, HashSet<string>>>? frameworksByModuleCodeLoader = null)
    {
        IsPresent = path is not null;
        Path = path;
        _frameworksByModuleCodeLoader = frameworksByModuleCodeLoader;
    }

    /// <summary>Registers a member of the <c>MET</c> domain (a metric code).</summary>
    public void AddMetMember(string memberCode) => _metMembers.Add(memberCode);

    /// <summary>Registers a dimension code with its domain code. The same code may be repeated
    /// (several versions of the common base): the first is kept — this is a dictionary of SHAPE
    /// ("does this code exist?"), not of unique identity.</summary>
    public void AddDimension(string dimensionCode, string? domainCode) =>
        _dimensionDomainCode.TryAdd(dimensionCode, domainCode);

    /// <summary>Registers a real (domain, member) pair — to validate the "(domain:member)" form.</summary>
    public void AddDomainMember(string domainCode, string memberCode) =>
        _domainMemberPairs.Add((domainCode, memberCode));

    /// <summary>Does the member <paramref name="memberCode"/> really exist in the domain
    /// <paramref name="domainCode"/>? Without a dictionary it can be neither confirmed nor denied:
    /// returns <see langword="true"/> (the caller keeps the conservative behaviour of accepting
    /// without validating).</summary>
    public bool IsValidDomainMember(string? domainCode, string? memberCode)
    {
        if (!IsPresent)
        {
            return true;
        }

        return domainCode is not null && memberCode is not null
            && _domainMemberPairs.Contains((domainCode, memberCode));
    }

    public SingleCodeResolution Resolve(string code, out string? domainCode)
    {
        domainCode = null;
        if (!IsPresent)
        {
            return SingleCodeResolution.Unknown;
        }

        if (_metMembers.Contains(code))
        {
            return SingleCodeResolution.Metric;
        }

        if (_dimensionDomainCode.TryGetValue(code, out var domain))
        {
            domainCode = domain;
            return SingleCodeResolution.Dimension;
        }

        return SingleCodeResolution.Unknown;
    }

    /// <summary>
    /// The reporting framework of a module, resolved through the dictionary:
    /// <c>mModule.ModuleCode</c> -&gt; <c>mTaxonomy</c> -&gt; <c>mReportingFramework.FrameworkCode</c>,
    /// matching the code case-insensitively (<c>mModule.ModuleCode</c> is the upper-cased
    /// <c>ModuleVersion.Code</c>). Used for layout file names that do not carry the framework (4.4).
    /// Returns <see langword="false"/> when there is no dictionary or the module is not in it; it
    /// never guesses. A module that the dictionary maps to more than one framework is an error.
    /// </summary>
    public bool TryResolveFramework(string moduleCode, out string frameworkCode)
    {
        frameworkCode = string.Empty;
        if (!IsPresent)
        {
            return false;
        }

        _frameworksByModuleCode ??= _frameworksByModuleCodeLoader?.Invoke()
            ?? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (!_frameworksByModuleCode.TryGetValue(moduleCode, out var frameworks))
        {
            return false;
        }

        if (frameworks.Count != 1)
        {
            throw new InvalidOperationException(
                $"The dictionary '{Path}' maps the module code '{moduleCode}' to more than one framework " +
                $"({string.Join(", ", frameworks.OrderBy(f => f, StringComparer.Ordinal))}): the framework of a layout file " +
                "cannot be resolved without guessing.");
        }

        frameworkCode = frameworks.Single();
        return true;
    }

    public void Dispose()
    {
        // The data is held in memory: nothing to release here.
    }
}
