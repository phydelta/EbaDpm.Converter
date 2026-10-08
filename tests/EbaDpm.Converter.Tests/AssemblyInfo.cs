using System.Runtime.Versioning;

// Windows-only x64: the tests call SchemaCreator, which is marked
// [SupportedOSPlatform("windows")] in EbaDpm.Converter.Core.
[assembly: SupportedOSPlatform("windows")]
