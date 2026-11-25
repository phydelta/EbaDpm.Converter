using System.Xml.Linq;

namespace EbaDpm.Converter.Native.Tests.ProjectGraph;

/// <summary>
/// The <c>ProjectReference</c> graph of a set of <c>.csproj</c> files, read from the XML itself (no
/// MSBuild evaluation): an edge exists for every <c>ProjectReference</c> item with an <c>Include</c>
/// attribute. Projects are identified by file name without extension.
/// </summary>
public sealed class ProjectReferenceGraph
{
    private readonly Dictionary<string, HashSet<string>> _edges;

    private ProjectReferenceGraph(Dictionary<string, HashSet<string>> edges) => _edges = edges;

    /// <summary>Every project that appears as a source (a file that was read) or as a target.</summary>
    public IReadOnlyCollection<string> Projects =>
        _edges.Keys.Concat(_edges.Values.SelectMany(v => v)).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The projects <paramref name="project"/> references directly.</summary>
    public IReadOnlyCollection<string> DirectReferences(string project) =>
        _edges.TryGetValue(project, out var targets) ? targets.ToList() : [];

    /// <summary>Reads every <c>*.csproj</c> under <paramref name="root"/> (bin and obj folders excluded).</summary>
    public static ProjectReferenceGraph Load(string root)
    {
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s is "bin" or "obj"))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(file);
            var targets = new HashSet<string>(StringComparer.Ordinal);
            var document = XDocument.Load(file);
            foreach (var item in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                var include = item.Attribute("Include")?.Value;
                if (!string.IsNullOrWhiteSpace(include))
                {
                    var normalised = include.Replace('\\', '/');
                    targets.Add(Path.GetFileNameWithoutExtension(normalised));
                }
            }

            edges[name] = targets;
        }

        return new ProjectReferenceGraph(edges);
    }

    /// <summary>
    /// Every project <paramref name="project"/> reaches through <c>ProjectReference</c> items, directly or
    /// transitively, with the chain that leads to it (for the failure message).
    /// </summary>
    public Dictionary<string, string> ReachableWithChain(string project)
    {
        var chains = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<(string Project, string Chain)>();
        queue.Enqueue((project, project));
        while (queue.Count > 0)
        {
            var (current, chain) = queue.Dequeue();
            foreach (var target in DirectReferences(current))
            {
                if (chains.ContainsKey(target) || string.Equals(target, project, StringComparison.Ordinal))
                {
                    continue;
                }

                var next = $"{chain} -> {target}";
                chains[target] = next;
                queue.Enqueue((target, next));
            }
        }

        return chains;
    }

    /// <summary>
    /// Returns one message per violated rule: <paramref name="project"/> must not reach any of
    /// <paramref name="forbidden"/>. An empty list means the rule holds.
    /// </summary>
    public IReadOnlyList<string> Violations(string project, params string[] forbidden)
    {
        var reachable = ReachableWithChain(project);
        return forbidden
            .Where(reachable.ContainsKey)
            .Select(f => $"{project} must not reference {f}, but it does: {reachable[f]}")
            .ToList();
    }
}
