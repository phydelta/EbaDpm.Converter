using System.Reflection;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Creates a REAL, empty <c>.accdb</c> (no user tables) through ADOX (<c>ADOX.Catalog</c>, late-bound
/// COM -- the same component that ACE OLEDB installs). It is the file used to provoke
/// <see cref="EbaDpm.Converter.Core.Access.SourceModelDetectionException"/> in
/// <c>SourceModelDetectorTests</c>: it opens fine through ACE OLEDB (unlike a SQLite <c>.db</c>
/// with a faked extension), but its catalog has neither <c>Domain</c> nor <c>Category</c> -- the
/// "neither of the two models" case.
/// </summary>
internal static class Dpm2TestAccdbFactory
{
    public static string CreateEmptyAccdb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"empty-neither-model_{Environment.ProcessId}_{Guid.NewGuid():N}.accdb");

        var catalogType = Type.GetTypeFromProgID("ADOX.Catalog")
            ?? throw new InvalidOperationException(
                "ADOX.Catalog is not registered on this machine; an empty .accdb cannot be created for the test.");

        var catalog = Activator.CreateInstance(catalogType)
            ?? throw new InvalidOperationException("ADOX.Catalog could not be instantiated.");

        try
        {
            var connectionString = $"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={path};";
            catalogType.InvokeMember(
                "Create",
                BindingFlags.InvokeMethod,
                binder: null,
                target: catalog,
                args: [connectionString]);
        }
        finally
        {
            if (System.Runtime.InteropServices.Marshal.IsComObject(catalog))
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(catalog);
            }
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"ADOX.Catalog.Create did not produce the expected file '{path}'.");
        }

        return path;
    }
}
