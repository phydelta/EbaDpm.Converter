namespace EbaDpm.Converter.Native.Schema;

/// <summary>
/// The tables of the Access database that the native output does not export: a CLOSED list of
/// 11 names, never a pattern (docs/target-schema-native.md, "Content"). A table the EBA adds in a
/// later publication is therefore exported and reported as drift. Access system tables
/// (<c>MSys*</c>) and temporary tables (names starting with <c>~</c>) are not user tables and
/// never reach this list: the catalogue reader does not read them.
/// </summary>
public static class NativeExclusions
{
    /// <summary>Change log, declared non-normative by the DPM metamodel documentation (section 4.2.5).</summary>
    public static IReadOnlyList<string> ChangeLogTables { get; } =
        ["ChangeLog", "ChangeLogAttribute", "User", "Role", "UserRole"];

    /// <summary>EBA working tables, not part of the metamodel.</summary>
    public static IReadOnlyList<string> WorkingTables { get; } =
        ["Aux_CellMapping", "Aux_CellStatus", "ModelViolations", "VarGeneration_Detail", "VarGeneration_Summary", "VariableGeneration"];

    /// <summary>The 11 excluded names, change log first.</summary>
    public static IReadOnlyList<string> ExcludedTables { get; } = [.. ChangeLogTables, .. WorkingTables];

    private static readonly HashSet<string> Names = new(ExcludedTables, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="tableName"/> is one of the 11 excluded tables (Access names are case-insensitive).</summary>
    public static bool IsExcluded(string tableName) => Names.Contains(tableName);
}
