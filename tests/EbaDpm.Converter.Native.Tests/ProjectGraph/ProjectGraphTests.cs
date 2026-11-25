namespace EbaDpm.Converter.Native.Tests.ProjectGraph;

/// <summary>
/// The source split keeps the shared project (Core) free of both pipelines, and the native pipeline
/// free of the distribution one, so that deleting <c>EbaDpm.Converter.Distribution</c> later cannot
/// break the native pipeline. The rule is read from the <c>.csproj</c> files (transitively).
/// </summary>
public sealed class ProjectGraphTests : IDisposable
{
    private const string Core = "EbaDpm.Converter.Core";
    private const string Distribution = "EbaDpm.Converter.Distribution";
    private const string Native = "EbaDpm.Converter.Native";
    private const string Cli = "EbaDpm.Converter.Cli";

    private readonly string _dir =
        Path.Join(Path.GetTempPath(), $"EbaDpm.ProjectGraph_{Environment.ProcessId}_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static string RealSrc => Path.Join(RepoPaths.RepositoryRoot, "src");

    private void WriteProject(string name, params string[] references)
    {
        var folder = Path.Join(_dir, "src", name);
        Directory.CreateDirectory(folder);
        var items = string.Concat(references.Select(r =>
            $"    <ProjectReference Include=\"..\\{r}\\{r}.csproj\" />\n"));
        File.WriteAllText(Path.Join(folder, name + ".csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n" + items + "  </ItemGroup>\n</Project>\n");
    }

    private ProjectReferenceGraph LoadSynthetic() => ProjectReferenceGraph.Load(Path.Join(_dir, "src"));

    [Fact]
    public void RealGraph_ReadsTheExpectedEdges_SoThatAZeroViolationIsNotVacuous()
    {
        var graph = ProjectReferenceGraph.Load(RealSrc);

        Assert.Contains(Core, graph.Projects);
        Assert.Contains(Distribution, graph.Projects);
        Assert.Contains(Native, graph.Projects);
        Assert.Contains(Core, graph.DirectReferences(Distribution));
        Assert.Contains(Core, graph.DirectReferences(Native));
        Assert.Contains(Distribution, graph.DirectReferences(Cli));
        Assert.Contains(Native, graph.DirectReferences(Cli));
    }

    [Fact]
    public void Core_ReferencesNeitherDistributionNorNative()
    {
        var violations = ProjectReferenceGraph.Load(RealSrc).Violations(Core, Distribution, Native);

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Native_DoesNotReferenceDistribution()
    {
        var violations = ProjectReferenceGraph.Load(RealSrc).Violations(Native, Distribution);

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    // Positive controls: the same check, on a synthetic graph that breaks each rule, must report it.

    [Fact]
    public void PositiveControl_NativeReferencingDistributionDirectly_IsReported()
    {
        WriteProject(Core);
        WriteProject(Distribution, Core);
        WriteProject(Native, Core, Distribution);

        var violation = Assert.Single(LoadSynthetic().Violations(Native, Distribution));

        Assert.Contains($"{Native} -> {Distribution}", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void PositiveControl_NativeReachingDistributionTransitively_IsReported()
    {
        WriteProject(Distribution);
        WriteProject(Core, Distribution);
        WriteProject(Native, Core);

        var graph = LoadSynthetic();

        // Native -> Core -> Distribution: Native breaks its rule, and Core breaks its own.
        var nativeViolation = Assert.Single(graph.Violations(Native, Distribution));
        Assert.Contains($"{Native} -> {Core} -> {Distribution}", nativeViolation, StringComparison.Ordinal);
        Assert.Single(graph.Violations(Core, Distribution, Native));
    }

    [Fact]
    public void PositiveControl_CoreReferencingNative_IsReported()
    {
        WriteProject(Native);
        WriteProject(Core, Native);
        WriteProject(Distribution, Core);

        var violation = Assert.Single(LoadSynthetic().Violations(Core, Distribution, Native));

        Assert.Contains($"{Core} -> {Native}", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void Control_ACleanSyntheticGraph_ReportsNothing()
    {
        WriteProject(Core);
        WriteProject(Distribution, Core);
        WriteProject(Native, Core);

        var graph = LoadSynthetic();

        Assert.Empty(graph.Violations(Core, Distribution, Native));
        Assert.Empty(graph.Violations(Native, Distribution));
    }
}
