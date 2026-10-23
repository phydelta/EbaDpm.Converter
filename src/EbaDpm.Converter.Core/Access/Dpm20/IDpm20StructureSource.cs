namespace EbaDpm.Converter.Core.Access.Dpm20;

/// <summary>
/// The part of the DPM 2.0 source that <c>Dpm20StructureLoader.Load</c> reads.
/// <see cref="Dpm20AccessReader"/> is the production implementation; the interface exists so that
/// the loader can run from in-memory rows. It declares nothing else: it is a seam, not a general
/// abstraction of the reader.
/// </summary>
public interface IDpm20StructureSource
{
    /// <inheritdoc cref="Dpm20AccessReader.CutoffReleaseCode"/>
    string CutoffReleaseCode { get; }

    /// <inheritdoc cref="Dpm20AccessReader.ReadTableGroups"/>
    IEnumerable<Dpm20TableGroupRow> ReadTableGroups();

    /// <inheritdoc cref="Dpm20AccessReader.ReadTableGroupCompositions"/>
    IEnumerable<Dpm20TableGroupCompositionRow> ReadTableGroupCompositions();
}
