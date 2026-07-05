using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Domain-facing friendship level used both on the wire (as JSON <c>"friend"</c> /
/// <c>"trusted_friend"</c>) and internally by the repositories for visibility gating.
///
/// <para>
/// The Scylla schema stores this as a <c>smallint</c>: <c>0 → friend</c>,
/// <c>1 → trusted_friend</c>. See <see cref="FriendshipLevelExtensions"/> for the
/// <see cref="short"/> ↔ enum mapping used at the persistence boundary.
/// </para>
/// </summary>
[JsonConverter(typeof(LowerCaseEnumJsonConverter<FriendshipLevel>))]
public enum FriendshipLevel
{
    Friend,
    TrustedFriend,
}

public static class FriendshipLevelExtensions
{
    /// <summary>
    /// Canonical wire representation used in JSON payloads, migrated Elixir logs, and
    /// legacy string-typed API surfaces.
    /// </summary>
    public static string ToWireValue(this FriendshipLevel level) => level switch
    {
        FriendshipLevel.Friend => "friend",
        FriendshipLevel.TrustedFriend => "trusted_friend",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    /// <summary>
    /// Best-effort parse of the wire representation. Unknown values return <c>null</c>
    /// so callers can treat them the same as "no friendship at all" — the visibility
    /// gate is fail-closed and downgrading a bad row to public would be a leak.
    /// </summary>
    public static FriendshipLevel? TryParseWireValue(string? value) => value switch
    {
        "friend" => FriendshipLevel.Friend,
        "trusted_friend" => FriendshipLevel.TrustedFriend,
        _ => null,
    };

    public static short ToCode(this FriendshipLevel level) => level switch
    {
        FriendshipLevel.Friend => 0,
        FriendshipLevel.TrustedFriend => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    /// <summary>
    /// Maps the persisted <c>level</c> smallint into the enum. Anything other than 1
    /// resolves to <see cref="FriendshipLevel.Friend"/> — the historical fail-safe for
    /// corrupt rows (a bad value must not silently grant trusted-tier visibility).
    /// </summary>
    public static FriendshipLevel FromCode(short code)
        => code == 1 ? FriendshipLevel.TrustedFriend : FriendshipLevel.Friend;
}
