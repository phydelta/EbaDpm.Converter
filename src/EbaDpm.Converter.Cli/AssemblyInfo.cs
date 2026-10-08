using System.Runtime.Versioning;

// Windows-only x64: the whole application assumes Windows because of its dependency on
// Microsoft.ACE.OLEDB.16.0 in EbaDpm.Converter.Core.
[assembly: SupportedOSPlatform("windows")]
