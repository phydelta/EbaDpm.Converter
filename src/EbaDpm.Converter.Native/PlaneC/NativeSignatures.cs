using EbaDpm.Converter.Core.Validation.PlaneC;

namespace EbaDpm.Converter.Native.PlaneC;

/// <summary>A native datapoint signature: the normalized terms of one cell, and why a cell has none.</summary>
internal sealed record NativeSignature(IReadOnlyList<string> Terms, string Canonical);

/// <summary>
/// The native datapoint signature of a cell, at one release R (the codes depend on R):
/// <c>MET(&lt;property code of VariableVersion.PropertyID&gt;)</c>, plus
/// <c>&lt;property code&gt;(&lt;item code&gt;)</c> for every pair of <c>VariableVersion.ContextID</c> in
/// <c>ContextComposition</c> except the pairs whose item is the default item at R (the metric is never
/// dropped), plus <c>&lt;property code&gt;(*)</c> for every key header of the table version. It is the
/// same set of terms as the 45-table <c>DPS</c> reached from the source, without the dictionary tables
/// in between. A code that does not exist at R is counted and sampled, and the variable has no
/// signature: nothing is guessed.
/// </summary>
internal sealed class NativeSignatureBuilder(NativeStructure structure, NativeItemCodes itemCodes, long releaseId)
{
    private const int MaxSamples = 20;

    private readonly Dictionary<long, string[]?> _termsByVariable = [];

    /// <summary>Distinct codes missing (or ambiguous) at the release, by kind, for the report.</summary>
    public Dictionary<string, long> MissingByKind { get; } = new(StringComparer.Ordinal);

    public List<string> MissingSamples { get; } = [];

    private readonly HashSet<(string Kind, long Id)> _reportedMissing = [];

    private readonly Dictionary<long, (List<string> Terms, bool Incomplete)> _keyTermsByTable = [];

    /// <summary>How a cell is left without a signature.</summary>
    public enum CellStatus
    {
        Ok,
        Excluded,
        WithoutVariable,
        MissingCode,
    }

    /// <summary>The signature of one cell of a table version, or the reason it has none.</summary>
    public (NativeSignature? Signature, CellStatus Status) SignatureOf(NativeTableData table, NativeCell cell)
    {
        if (cell.IsExcluded)
        {
            return (null, CellStatus.Excluded);
        }

        if (cell.VariableVid is not { } variableVid)
        {
            return (null, CellStatus.WithoutVariable);
        }

        var (keyTerms, keyIncomplete) = KeyTermsOf(table);
        var variableTerms = TermsOf(variableVid);
        if (variableTerms is null || keyIncomplete)
        {
            return (null, CellStatus.MissingCode);
        }

        var terms = new HashSet<string>(variableTerms, StringComparer.Ordinal);
        foreach (var keyTerm in keyTerms)
        {
            terms.Add(keyTerm);
        }

        return (new NativeSignature(terms.ToList(), PlaneCSignature.Canonicalize(terms)), CellStatus.Ok);
    }

    /// <summary>The signatures of the cells of a table version that have one, and how many cells lack
    /// one because of a missing code or because they have no variable.</summary>
    public (List<NativeSignature> Signatures, long CellsWithMissingCode, long CellsWithoutVariable) Build(NativeTableData table)
    {
        var signatures = new List<NativeSignature>();
        long missing = 0;
        long withoutVariable = 0;
        foreach (var cell in table.Cells)
        {
            var (signature, status) = SignatureOf(table, cell);
            switch (status)
            {
                case CellStatus.Ok:
                    signatures.Add(signature!);
                    break;
                case CellStatus.MissingCode:
                    missing++;
                    break;
                case CellStatus.WithoutVariable:
                    withoutVariable++;
                    break;
            }
        }

        return (signatures, missing, withoutVariable);
    }

    /// <summary>One <c>&lt;property code&gt;(*)</c> term per key header of the table version.</summary>
    private (List<string> Terms, bool Incomplete) KeyTermsOf(NativeTableData table)
    {
        if (_keyTermsByTable.TryGetValue(table.TableVid, out var cached))
        {
            return cached;
        }

        var terms = new List<string>();
        var incomplete = false;
        foreach (var header in table.Headers.Where(h => h.IsKey))
        {
            if (header.PropertyId is not { } propertyId)
            {
                continue;
            }

            if (TryCode(propertyId, "key header property", out var code, out _))
            {
                terms.Add(PlaneCSignature.NormalizeTerm(code + "(*)"));
            }
            else
            {
                incomplete = true;
            }
        }

        _keyTermsByTable[table.TableVid] = (terms, incomplete);
        return (terms, incomplete);
    }

    /// <summary>The terms of a variable version, or <see langword="null"/> when a code is missing at R.</summary>
    public string[]? TermsOf(long variableVid)
    {
        if (_termsByVariable.TryGetValue(variableVid, out var cached))
        {
            return cached;
        }

        string[]? result = null;
        if (structure.Variables.TryGetValue(variableVid, out var variable))
        {
            var terms = new List<string>();
            var complete = true;

            if (variable.PropertyId is { } metricId)
            {
                if (TryCode(metricId, "metric property", out var metricCode, out _))
                {
                    terms.Add(PlaneCSignature.NormalizeTerm("MET(" + metricCode + ")"));
                }
                else
                {
                    complete = false;
                }
            }

            if (variable.ContextId is { } contextId && structure.Contexts.TryGetValue(contextId, out var pairs))
            {
                foreach (var (propertyId, itemId) in pairs)
                {
                    if (!TryCode(propertyId, "context property", out var propertyCode, out _)
                        || !TryCode(itemId, "context item", out var itemCode, out var isDefault))
                    {
                        complete = false;
                        continue;
                    }

                    if (isDefault)
                    {
                        continue; // default members are not part of a signature; the metric never is one.
                    }

                    terms.Add(PlaneCSignature.NormalizeTerm(propertyCode + "(" + itemCode + ")"));
                }
            }

            result = complete ? terms.ToArray() : null;
        }
        else
        {
            Report("variable version", variableVid);
        }

        _termsByVariable[variableVid] = result;
        return result;
    }

    private bool TryCode(long itemId, string kind, out string code, out bool isDefault)
    {
        var status = itemCodes.TryGet(itemId, releaseId, out code, out isDefault);
        if (status == ItemCodeStatus.Found)
        {
            return true;
        }

        Report(status == ItemCodeStatus.Ambiguous ? kind + " (several rows valid)" : kind, itemId);
        return false;
    }

    private void Report(string kind, long id)
    {
        if (!_reportedMissing.Add((kind, id)))
        {
            return;
        }

        MissingByKind[kind] = MissingByKind.GetValueOrDefault(kind) + 1;
        if (MissingSamples.Count < MaxSamples)
        {
            MissingSamples.Add($"{kind} ID={id} cannot be resolved at the release");
        }
    }
}
