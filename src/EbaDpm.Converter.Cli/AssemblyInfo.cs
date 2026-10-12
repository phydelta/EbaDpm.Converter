using System.Runtime.Versioning;

// Windows-only, x64 or x86: the whole application assumes Windows because of its dependency on
// ACE OLEDB (16.0 or 12.0) in EbaDpm.Converter.Core.
[assembly: SupportedOSPlatform("windows")]
