namespace EbaDpm.Converter.Native.PlaneC;

/// <summary>One known divergence: ONE object, identified by its exact business key, excluded from ONE
/// check, with the reason. Never applied by pattern, table or prefix.</summary>
/// <param name="Id"><c>NDD-23</c> for a <c>C-DPS-01</c> signature, <c>NDD-27</c> for a <c>C-MHR-01</c> unit.</param>
/// <param name="Check"><c>C-DPS-01</c> or <c>C-MHR-01</c>.</param>
/// <param name="BusinessKey"><c>{TableCode}\t{LayoutSignature}</c> (<c>C-DPS-01</c>) or
/// <c>{TableCode}\t{Release}|{Axis}|{OrdinateCode}|{MetricCode}</c> (<c>C-MHR-01</c>).</param>
internal sealed record NativeDivergence(string Id, string Check, string BusinessKey, string Reason);

/// <summary>The census of one release: the divergences measured on the layouts of that release.</summary>
internal sealed record NativePlaneCCensus(string CorpusRelease, string SourceName, IReadOnlyList<NativeDivergence> Entries);

/// <summary>
/// The plane C censuses of the native output: one file per release,
/// <c>Resources/native-plane-c-known-divergences-&lt;release&gt;.tsv</c>, embedded in the assembly. The
/// format is that of the distribution censuses: a <c># CorpusRelease: &lt;release&gt;</c> header (mandatory),
/// comment lines starting with <c>#</c>, the column header <c>TableCode\tType\tLayoutSignature</c>, and
/// then
/// <list type="bullet">
/// <item><c>TableCode\tcontradicts|no-match\tLayoutSignature[\tReason]</c>: a <c>C-DPS-01</c>
/// violation. The type is a diagnosis, not part of the key.</item>
/// <item><c>TableCode\thierarchy\t{Release}|{Axis}|{OrdinateCode}|{MetricCode}\tReason</c>: a
/// <c>C-MHR-01</c> violation; the reason is mandatory.</item>
/// </list>
/// No file means an empty census, which exempts nothing.
/// </summary>
internal static class NativePlaneCCensuses
{
    public const string ResourcePrefix = "EbaDpm.Converter.Native.Resources.native-plane-c-known-divergences-";

    public const string ResourceSuffix = ".tsv";

    public const string DpsId = "NDD-23";

    public const string HierarchyId = "NDD-27";

    private static readonly Lazy<IReadOnlyList<NativePlaneCCensus>> Embedded = new(LoadEmbedded);

    public static IReadOnlyList<NativePlaneCCensus> EmbeddedCensuses => Embedded.Value;

    private static IReadOnlyList<NativePlaneCCensus> LoadEmbedded()
    {
        var assembly = typeof(NativePlaneCCensuses).Assembly;
        var sources = new List<(string Name, string Text)>();
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                                 && n.EndsWith(ResourceSuffix, StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            sources.Add(("native-plane-c-known-divergences-" + resourceName[ResourcePrefix.Length..], reader.ReadToEnd()));
        }

        return LoadAll(sources);
    }

    /// <summary>Parses a set of census files (name, text). Every census declares its release, and two
    /// censuses may not declare the same one: both are errors, never resolved by picking one.</summary>
    public static IReadOnlyList<NativePlaneCCensus> LoadAll(IEnumerable<(string Name, string Text)> sources)
    {
        var censuses = new List<NativePlaneCCensus>();
        var nameByRelease = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, text) in sources)
        {
            var census = Parse(text, name);
            if (nameByRelease.TryGetValue(census.CorpusRelease, out var other))
            {
                throw new InvalidOperationException(
                    $"Plane C censuses '{other}' and '{name}' both declare '# CorpusRelease: {census.CorpusRelease}': one census per release.");
            }

            nameByRelease[census.CorpusRelease] = name;
            censuses.Add(census);
        }

        return censuses;
    }

    public static NativePlaneCCensus Parse(string tsvText, string sourceName = "native-plane-c-known-divergences.tsv")
    {
        var fileReason =
            "Native plane C census of the live violations against the EBA Annotated Table Layouts " +
            $"(src/EbaDpm.Converter.Native/Resources/{sourceName}), arbitrated against the Access database. " +
            "Regenerated per release, never relaxed by threshold.";
        var entries = new List<NativeDivergence>();
        string? corpusRelease = null;
        using var reader = new StringReader(tsvText);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("# CorpusRelease:", StringComparison.Ordinal))
            {
                corpusRelease = line["# CorpusRelease:".Length..].Trim().Split(' ', 2)[0];
                continue;
            }

            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("TableCode\t", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length >= 2 && string.Equals(parts[1], "hierarchy", StringComparison.Ordinal))
            {
                if (parts.Length != 4 || string.IsNullOrWhiteSpace(parts[3]) || string.IsNullOrWhiteSpace(parts[0])
                    || string.IsNullOrWhiteSpace(parts[2]))
                {
                    throw new InvalidOperationException(
                        $"Malformed row in {sourceName}: '{line}' - a 'hierarchy' row expects 4 tab-separated columns " +
                        "(TableCode, hierarchy, {Release}|{Axis}|{OrdinateCode}|{MetricCode}, Reason) with a non-empty reason.");
                }

                entries.Add(new NativeDivergence(HierarchyId, "C-MHR-01", $"{parts[0]}\t{parts[2]}", parts[3]));
                continue;
            }

            var wrongColumnCount = parts.Length != 3 && parts.Length != 4;
            var missingRequiredField = string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[2]);
            var unknownType = parts[1] != "contradicts" && parts[1] != "no-match";
            var emptyReason = parts.Length == 4 && string.IsNullOrWhiteSpace(parts[3]);
            if (wrongColumnCount || missingRequiredField || unknownType || emptyReason)
            {
                throw new InvalidOperationException(
                    $"Malformed row in {sourceName}: '{line}' - expected TableCode, Type (contradicts|no-match), " +
                    "LayoutSignature and, optionally, a non-empty Reason (or a hierarchy row).");
            }

            var reason = parts.Length == 4 ? parts[3] : fileReason;
            entries.Add(new NativeDivergence(DpsId, "C-DPS-01", $"{parts[0]}\t{parts[2]}", reason));
        }

        if (string.IsNullOrEmpty(corpusRelease))
        {
            throw new InvalidOperationException(
                $"Plane C census '{sourceName}' does not declare its release: a '# CorpusRelease: <release>' header is required.");
        }

        return new NativePlaneCCensus(corpusRelease, sourceName, entries);
    }

    /// <summary>The violations of one check in one release, minus the divergences the census of that
    /// release declares. A declared divergence that no longer violates is stale and comes back as an
    /// unresolved item. <paramref name="matchedKeys"/> are the declared keys that still violate.</summary>
    public static (List<string> Unresolved, HashSet<string> MatchedKeys) Apply(
        string check, NativePlaneCCensus? census, IReadOnlySet<string> violatingKeys)
    {
        var declared = census?.Entries.Where(e => e.Check == check).ToList() ?? [];
        var declaredKeys = declared.Select(e => e.BusinessKey).ToHashSet(StringComparer.Ordinal);
        var unresolved = violatingKeys.Except(declaredKeys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in declared)
        {
            if (violatingKeys.Contains(entry.BusinessKey))
            {
                matched.Add(entry.BusinessKey);
            }
            else
            {
                unresolved.Add($"(absent, {entry.Id} no longer violates) {entry.BusinessKey}");
            }
        }

        return (unresolved, matched);
    }
}
