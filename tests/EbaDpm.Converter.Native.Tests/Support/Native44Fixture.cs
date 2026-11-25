using EbaDpm.Converter.Cli;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.Support;

[CollectionDefinition(Name)]
public sealed class Native44Collection : ICollectionFixture<Native44Fixture>
{
    public const string Name = "Native44";
}

/// <summary>
/// One full native conversion of the 4.4 database through the CLI (about 45 s), shared by the data
/// tests. Does nothing when the data directory or the file is not available, or ACE is missing: the
/// tests are skipped in that case.
/// </summary>
public sealed class Native44Fixture : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), $"native44_{Environment.ProcessId}_{Guid.NewGuid():N}");

    public string SourcePath => RepoPaths.AccessDpm20Release44DatabasePath;

    public string OutputPath => Path.Join(_directory, "native44.db");

    public int ExitCode { get; private set; } = -1;

    public string Stdout { get; private set; } = string.Empty;

    public string Stderr { get; private set; } = string.Empty;

    public bool Converted { get; private set; }

    public Native44Fixture()
    {
        if (!RepoPaths.IsDataAvailable || !File.Exists(SourcePath) || !AceRequirement.IsAvailable)
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        ExitCode = CliRunner.Run(
            ["--source", SourcePath, "--output", OutputPath, "--target", "native", "--plain"], stdout, stderr);
        Stdout = stdout.ToString();
        Stderr = stderr.ToString();
        Converted = true;

        // The conversion went through pooled OLE DB connections: release them so the 4.4 file is not left locked.
        TempFiles.ReleaseAccess();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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
