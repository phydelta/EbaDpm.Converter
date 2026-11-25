using System.Text.Json;

namespace EbaDpm.Converter.Native.Catalog;

/// <summary>
/// The baseline of the native output: the full catalogue of the DPM 2.0 Access database of one
/// release (4.4), embedded in the assembly as <c>Resources/native-baseline-4.4.json</c>. It holds
/// every user table, the excluded ones too, so that a drift report can compare complete
/// catalogues. <see cref="ReleaseCode"/> is the <c>Release.Code</c> a source must contain to be
/// converted; <see cref="SourceFile"/> is the Access file the catalogue was read from.
/// </summary>
public sealed record NativeBaseline(string ReleaseCode, string SourceFile, NativeCatalog Catalog)
{
    private const string ResourceName = "EbaDpm.Converter.Native.Resources.native-baseline-4.4.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly Lazy<NativeBaseline> Embedded = new(Read);

    /// <summary>The embedded baseline (read once).</summary>
    public static NativeBaseline Load() => Embedded.Value;

    /// <summary>Stable JSON text (indented, LF line ends, final newline).</summary>
    public string ToJson()
        => JsonSerializer.Serialize(this, Options).ReplaceLineEndings("\n") + "\n";

    public static NativeBaseline FromJson(string json)
        => JsonSerializer.Deserialize<NativeBaseline>(json, Options)
            ?? throw new InvalidOperationException("The native baseline JSON is empty.");

    private static NativeBaseline Read()
    {
        using var stream = typeof(NativeBaseline).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found in the assembly. "
                + "Check the EmbeddedResource item in the .csproj.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return FromJson(reader.ReadToEnd());
    }
}
