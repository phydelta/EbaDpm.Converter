using System.Runtime.Versioning;

// Windows-only x64: reading the source depends on Microsoft.ACE.OLEDB.16.0, which is available
// only on Windows. This attribute tells the platform-compatibility analyzer (CA1416) that the
// whole assembly assumes Windows, avoiding false positives when calling System.Data.OleDb.
[assembly: SupportedOSPlatform("windows")]
