using System.Runtime.Versioning;

// Windows-only, x64 or x86: the source is read through ACE OLEDB (16.0 or 12.0), which is
// available only on Windows. This attribute tells the platform-compatibility analyzer (CA1416) that the
// whole assembly assumes Windows.
[assembly: SupportedOSPlatform("windows")]
