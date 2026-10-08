using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Layouts;

/// <summary>What a single-term code "(A)" is, according to what really exists in the
/// dictionary (it is NOT decided by the shape of the text).</summary>
internal enum SingleCodeResolution
{
    /// <summary>Not a real DPM code (neither a member of <c>MET</c> nor a dimension): a rhetorical
    /// parenthesis such as "(a)", "(EU)", "(call)" — it is not an annotation, and it is not recorded
    /// in <c>LayoutUnparsed</c>.</summary>
    Unknown,

    /// <summary>Member of the <c>MET</c> domain: the metric of the <c>Main Property</c> column.</summary>
    Metric,

    /// <summary>Code of <c>mDimension</c>: the typical dimension of the key column of an open axis
    /// (case <c>C_106.00</c> <c>D8=(PBE)</c>).</summary>
    Dimension,
}

/// <summary>
/// Dictionary of DPM codes (<c>mDimension</c>/<c>mDomain</c>/<c>mMember</c>) against which the
/// single-code ambiguity is resolved. It relies on ANY SQLite database with the distribution
/// schema: a reference database, or the converter's own generated output — the extractor does not
/// impose which one, because this is a MECHANICAL query of which codes exist, not an arbitration
/// of content (disputes about the VALUE of a cell are a different matter from which table to use as
/// the list of valid codes to disambiguate a syntactic shape).
///
/// Without a dictionary (<paramref name="path"/> null), every single-term code resolves to
/// <see cref="SingleCodeResolution.Unknown"/>: the caller decides the conservative default
/// (treat it as a metric, with a warning) — see <c>LayoutSheetParser</c>.
/// </summary>
internal sealed class LayoutDictionary : IDisposable
{
    private readonly HashSet<string> _metMembers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _dimensionDomainCode = new(StringComparer.Ordinal);
    private readonly HashSet<(string DomainCode, string MemberCode)> _domainMemberPairs = new();

    public bool IsPresent { get; }

    public string? Path { get; }

    public LayoutDictionary(string? path)
    {
        IsPresent = path is not null;
        Path = path;
        if (path is null)
        {
            return;
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT m."MemberCode"
                FROM "mMember" m
                JOIN "mDomain" d ON d."DomainID" = m."DomainID"
                WHERE d."DomainCode" = 'MET' AND m."MemberCode" IS NOT NULL
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                _metMembers.Add(reader.GetString(0));
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT dim."DimensionCode", dom."DomainCode"
                FROM "mDimension" dim
                LEFT JOIN "mDomain" dom ON dom."DomainID" = dim."DomainID"
                WHERE dim."DimensionCode" IS NOT NULL
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var code = reader.GetString(0);
                var domainCode = reader.IsDBNull(1) ? null : reader.GetString(1);
                // The same DimensionCode may be repeated (several versions of the common base):
                // if there was already one, the first is kept — this is a dictionary of SHAPE
                // ("does this code exist?"), not of unique identity.
                _dimensionDomainCode.TryAdd(code, domainCode);
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            // Real (domain, member) pairs — to validate the "(domain:member)" form: a two-code
            // value paired POSITIONALLY with a declaration is not, by that alone, a real DPM
            // value. "ZZFAKE:ZZFAKE2" has the right shape and neither code exists.
            cmd.CommandText =
                """
                SELECT dom."DomainCode", m."MemberCode"
                FROM "mMember" m
                JOIN "mDomain" dom ON dom."DomainID" = m."DomainID"
                WHERE dom."DomainCode" IS NOT NULL AND m."MemberCode" IS NOT NULL
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                _domainMemberPairs.Add((reader.GetString(0), reader.GetString(1)));
            }
        }
    }

    /// <summary>Does the member <paramref name="memberCode"/> really exist in the domain
    /// <paramref name="domainCode"/>? Without a dictionary it can be neither confirmed nor denied:
    /// returns <see langword="true"/> (the caller keeps the conservative behaviour of accepting
    /// without validating).</summary>
    public bool IsValidDomainMember(string? domainCode, string? memberCode)
    {
        if (!IsPresent)
        {
            return true;
        }

        return domainCode is not null && memberCode is not null
            && _domainMemberPairs.Contains((domainCode, memberCode));
    }

    public SingleCodeResolution Resolve(string code, out string? domainCode)
    {
        domainCode = null;
        if (!IsPresent)
        {
            return SingleCodeResolution.Unknown;
        }

        if (_metMembers.Contains(code))
        {
            return SingleCodeResolution.Metric;
        }

        if (_dimensionDomainCode.TryGetValue(code, out var domain))
        {
            domainCode = domain;
            return SingleCodeResolution.Dimension;
        }

        return SingleCodeResolution.Unknown;
    }

    public void Dispose()
    {
        // Connections are opened and closed inside the constructor: nothing to release here.
    }
}
