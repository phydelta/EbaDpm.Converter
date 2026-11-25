using System.Data.OleDb;

namespace EbaDpm.Converter.Native.Tests.Support;

internal static class TempFiles
{
    /// <summary>
    /// Releases every pooled OLE DB connection of this process, so that no Access file stays locked for the
    /// tests of other classes that copy or open it.
    /// </summary>
    public static void ReleaseAccess()
    {
        OleDbConnection.ReleaseObjectPool();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    /// <summary>Deletes a temporary Access file after releasing the OLE DB pool; a file still locked is left to the OS temp cleanup.</summary>
    public static void TryDeleteAccdb(string path)
    {
        OleDbConnection.ReleaseObjectPool();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left for the OS temp cleanup.
        }
    }
}
