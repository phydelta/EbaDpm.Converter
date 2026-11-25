using EbaDpm.Converter.Native.Conversion;

namespace EbaDpm.Converter.Native.Tests.Support;

/// <summary>
/// One synthetic DPM 2.0 source (releases 4.3 and 4.4) converted once with <see cref="NativeConverter"/>.
/// Does nothing when the Access Database Engine is not installed (the tests are <c>[AceFact]</c>).
/// </summary>
public sealed class SyntheticConversionFixture : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), $"native-synth_{Environment.ProcessId}_{Guid.NewGuid():N}");

    internal SyntheticDpm2Accdb? Source { get; private set; }

    public string OutputPath => Path.Join(_directory, "out.db");

    public NativeConversionSummary? Summary { get; private set; }

    public string SourcePath => Source?.Path ?? throw new InvalidOperationException("The Access Database Engine is not available.");

    public SyntheticConversionFixture()
    {
        if (!AceRequirement.IsAvailable)
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        Source = SyntheticDpm2Accdb.Create();
        Summary = NativeConverter.Convert(Source.Path, OutputPath, overwrite: false);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Source?.Dispose();
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Left for the OS temp cleanup.
        }
    }
}
