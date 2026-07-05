using Interfold.Contracts.Enums;

namespace Interfold.Contracts.Models;

/// <summary>
/// Shared persistence-code mapping and viewer gating for <see cref="VisibilityLevel"/>,
/// replacing the per-repository copies that used to live in each Scylla/InMemory
/// repository. The smallint codes match the enum's declared values (0–3) and are frozen
/// by the Scylla schema.
/// </summary>
public static class VisibilityLevelExtensions
{
    public static short ToCode(this VisibilityLevel level) => (short)level;

    /// <summary>
    /// Read-side mapping for alter/tag/front rows: null and unknown codes resolve to
    /// <see cref="VisibilityLevel.Public"/> for backward compatibility with older rows
    /// that pre-date the column.
    /// </summary>
    public static VisibilityLevel FromCodeOrPublic(short? code) => code switch
    {
        1 => VisibilityLevel.FriendsOnly,
        2 => VisibilityLevel.TrustedOnly,
        3 => VisibilityLevel.Private,
        _ => VisibilityLevel.Public,
    };

    /// <summary>
    /// Read-side mapping for settings-field rows: unknown codes resolve to
    /// <see cref="VisibilityLevel.Private"/> — field definitions fail closed because a
    /// corrupt row must not leak a private field to friends.
    /// </summary>
    public static VisibilityLevel FromCodeOrPrivate(short code) => code switch
    {
        0 => VisibilityLevel.Public,
        1 => VisibilityLevel.FriendsOnly,
        2 => VisibilityLevel.TrustedOnly,
        _ => VisibilityLevel.Private,
    };

    /// <summary>
    /// The single visibility gate: whether a viewer with <paramref name="friendshipLevel"/>
    /// (null = not a friend) may see an entity at this level. Fail-closed for unknown levels.
    /// </summary>
    public static bool CanBeViewedBy(this VisibilityLevel visibilityLevel, FriendshipLevel? friendshipLevel)
        => visibilityLevel switch
        {
            VisibilityLevel.Public => true,
            VisibilityLevel.FriendsOnly => friendshipLevel is FriendshipLevel.Friend or FriendshipLevel.TrustedFriend,
            VisibilityLevel.TrustedOnly => friendshipLevel is FriendshipLevel.TrustedFriend,
            _ => false,
        };
}
