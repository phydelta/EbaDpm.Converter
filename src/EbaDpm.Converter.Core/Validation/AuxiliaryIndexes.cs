using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation;

/// <summary>
/// The 8 auxiliary indexes the plane A battery needs: without them, running plane A over an
/// <c>--all</c> conversion does NOT FINISH (it was still running after 10 minutes). With them,
/// building takes under a second and the whole of plane A drops below a minute.
///
/// They are ALWAYS created on a TEMPORARY COPY of the generated database, never on the product
/// output file: the output is a schema contract and these indexes are not part of it.
/// </summary>
/// </summary>
public static class AuxiliaryIndexes
{
    private static readonly string[] CreateStatements =
    [
        "CREATE INDEX IF NOT EXISTS \"IX_val_AxisOrdinate_AxisID\" ON \"mAxisOrdinate\" (\"AxisID\")",
        "CREATE INDEX IF NOT EXISTS \"IX_val_AxisOrdinate_ParentOrdinateID\" ON \"mAxisOrdinate\" (\"ParentOrdinateID\")",
        "CREATE INDEX IF NOT EXISTS \"IX_val_CellPosition_OrdinateID\" ON \"mCellPosition\" (\"OrdinateID\")",
        "CREATE INDEX IF NOT EXISTS \"IX_val_TableCell_TableID\" ON \"mTableCell\" (\"TableID\")",
        "CREATE INDEX IF NOT EXISTS \"IX_val_OrdinateCategorisation_MemberID\" ON \"mOrdinateCategorisation\" (\"MemberID\")",
        "CREATE INDEX IF NOT EXISTS \"IX_val_TableAxis_TableID\" ON \"mTableAxis\" (\"TableID\")",
        "CREATE INDEX IF NOT EXISTS \"IX_val_HierarchyNode_ParentMemberID\" ON \"mHierarchyNode\" (\"HierarchyID\", \"ParentMemberID\")",
        "CREATE INDEX IF NOT EXISTS \"IX_val_Member_DomainID\" ON \"mMember\" (\"DomainID\")",
    ];

    public static long Create(SqliteConnection connection)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var statement in CreateStatements)
        {
            SqlHelpers.Execute(connection, statement);
        }

        sw.Stop();
        return sw.ElapsedMilliseconds;
    }
}
