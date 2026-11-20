using EbaDpm.Converter.Core.Access;

namespace EbaDpm.Converter.Tests.Dictionary;

/// <summary>
/// In-memory <see cref="IDpmSourceReader"/> over a few DPM 1.0 (Access) rows, for the synthetic
/// tests of <c>AxisAndCellLoader</c>. Only the reads of that loader are populated; every other
/// read throws, so that a test never silently depends on a source it did not declare.
/// </summary>
internal sealed class InMemoryDpm1AxisAndCellSource : IDpmSourceReader
{
    public List<AccessOwnerRow> Owners { get; } = [];
    public List<AccessMemberRow> Members { get; } = [];
    public List<AccessDimensionRow> Dimensions { get; } = [];
    public List<AccessMetricRow> Metrics { get; } = [];
    public List<AccessHierarchyRow> Hierarchies { get; } = [];
    public List<AccessAxisRow> Axes { get; } = [];
    public List<AccessAxisOrdinateRow> Ordinates { get; } = [];
    public List<AccessOrdinateCategorisationRow> Categorisations { get; } = [];
    public List<AccessTableCellRow> Cells { get; } = [];
    public List<AccessCellPositionRow> CellPositions { get; } = [];
    public List<AccessOpenMemberRestrictionRow> OpenMemberRestrictions { get; } = [];

    public void Open()
    {
    }

    public void Close()
    {
    }

    public void Dispose()
    {
    }

    public IEnumerable<AccessOwnerRow> ReadOwners() => Owners;

    public IEnumerable<AccessMemberRow> ReadMembers() => Members;

    public IEnumerable<AccessDimensionRow> ReadDimensions() => Dimensions;

    public IEnumerable<AccessMetricRow> ReadMetrics() => Metrics;

    public IEnumerable<AccessHierarchyRow> ReadHierarchies() => Hierarchies;

    public IEnumerable<AccessAxisRow> ReadAxesByTableVIds(IEnumerable<int> tableVIds)
        => Axes.Where(a => a.TableVId is { } v && tableVIds.Contains(v));

    public IEnumerable<AccessAxisOrdinateRow> ReadAxisOrdinatesByTableVIds(IEnumerable<int> tableVIds)
    {
        var axisIds = ReadAxesByTableVIds(tableVIds).Select(a => a.AxisId).ToHashSet();
        return Ordinates.Where(o => axisIds.Contains(o.AxisId));
    }

    public IEnumerable<AccessOrdinateCategorisationRow> ReadOrdinateCategorisationsByTableVIds(IEnumerable<int> tableVIds)
    {
        var ordinateIds = ReadAxisOrdinatesByTableVIds(tableVIds).Select(o => o.OrdinateId).ToHashSet();
        return Categorisations.Where(c => ordinateIds.Contains(c.OrdinateId));
    }

    public IEnumerable<AccessTableCellRow> ReadTableCellsByTableVIds(IEnumerable<int> tableVIds)
        => Cells.Where(c => c.TableVId is { } v && tableVIds.Contains(v));

    public IEnumerable<AccessCellPositionRow> ReadCellPositionsByTableVIds(IEnumerable<int> tableVIds)
    {
        var cellIds = ReadTableCellsByTableVIds(tableVIds).Select(c => c.CellId).ToHashSet();
        return CellPositions.Where(p => cellIds.Contains(p.CellId)).OrderBy(p => p.CellId);
    }

    public IEnumerable<AccessOpenMemberRestrictionRow> ReadOpenMemberRestrictions() => OpenMemberRestrictions;

    // ---- not used by AxisAndCellLoader ----

    public IEnumerable<AccessReportingFrameworkRow> ReadReportingFrameworks() => throw NotDeclared();

    public IEnumerable<AccessTaxonomyRow> ReadTaxonomies() => throw NotDeclared();

    public IEnumerable<AccessConceptRow> ReadConceptsByIds(IEnumerable<int> conceptIds) => throw NotDeclared();

    public int ReadMaxConceptId() => throw NotDeclared();

    public IEnumerable<AccessTaxonomyTableCount> ReadTaxonomyTableCounts() => throw NotDeclared();

    public IEnumerable<AccessDomainRow> ReadDomains() => throw NotDeclared();

    public IEnumerable<AccessHierarchyNodeRow> ReadHierarchyNodes() => throw NotDeclared();

    public IEnumerable<AccessDataTypeRow> ReadDataTypes() => throw NotDeclared();

    public IEnumerable<AccessFlowTypeRow> ReadFlowTypes() => throw NotDeclared();

    public IEnumerable<AccessTableGroupRow> ReadTableGroups() => throw NotDeclared();

    public IEnumerable<AccessTemplateRow> ReadTemplates() => throw NotDeclared();

    public IEnumerable<AccessTaxonomyTableVersionRow> ReadTaxonomyTableVersions() => throw NotDeclared();

    public IEnumerable<AccessTableVersionRow> ReadTableVersionsByIds(IEnumerable<int> tableVIds) => throw NotDeclared();

    public IEnumerable<AccessTableRow> ReadTablesByIds(IEnumerable<int> tableIds) => throw NotDeclared();

    public IEnumerable<AccessModuleRow> ReadModules() => throw NotDeclared();

    public IEnumerable<AccessModuleTableVersionRow> ReadModuleTableVersions() => throw NotDeclared();

    public IEnumerable<AccessOpenAxisValueRestrictionRow> ReadOpenAxisValueRestrictions() => throw NotDeclared();

    private static InvalidOperationException NotDeclared()
        => new("This read is not used by AxisAndCellLoader and is not populated by the synthetic source.");
}
