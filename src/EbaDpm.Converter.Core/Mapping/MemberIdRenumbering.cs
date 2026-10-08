namespace EbaDpm.Converter.Core.Mapping;

/// <summary>
/// Translation of an Access <c>MemberID</c> to the <c>MemberID</c> emitted in the target.
///
/// ID 9999 is RESERVED in the target for the sentinel member "Open". In Access v4.1 that ID is
/// already used by a real member (<c>C27_12</c>, NACE, domain 280): this class is the ONLY place
/// where its renumbering is decided and applied. Every target column that carries an Access
/// <c>MemberID</c> (<c>mHierarchyNode.MemberID</c>/<c>ParentMemberID</c> and
/// <c>mMember.MemberID</c>, plus <c>mOrdinateCategorisation.MemberID</c> and
/// <c>mOpenAxisValueRestriction.HierarchyStartingMemberID</c>) must go through
/// <see cref="Translate"/> instead of copying the Access ID verbatim. That way, if Access changes
/// and leaves 9999 free (or another member takes it), a single place decides the translation and
/// no table can silently forget it.
/// </summary>
public sealed class MemberIdRenumbering
{
    private readonly IReadOnlyDictionary<int, int> _translations;

    private MemberIdRenumbering(IReadOnlyDictionary<int, int> translations)
    {
        _translations = translations;
    }

    /// <summary>No renumbering: Access 9999 was free and does not displace any member.</summary>
    public static readonly MemberIdRenumbering Identity = new(new Dictionary<int, int>());

    /// <summary>
    /// Access had a real member at <paramref name="reservedId"/> (the ID reserved by the
    /// sentinel); it is renumbered to <paramref name="newId"/>.
    /// </summary>
    public static MemberIdRenumbering ForOccupant(int reservedId, int newId)
        => new(new Dictionary<int, int> { [reservedId] = newId });

    /// <summary>
    /// Translates an Access <c>MemberID</c>: the renumbered occupant if it matches the reserved
    /// ID, any other <c>MemberID</c> verbatim.
    /// </summary>
    public int Translate(int memberId)
        => _translations.TryGetValue(memberId, out var translated) ? translated : memberId;

    /// <summary>Like <see cref="Translate"/>, but for nullable <c>MemberID</c> columns.</summary>
    public int? TranslateNullable(int? memberId)
        => memberId is { } id ? Translate(id) : null;
}
