namespace Interfold.StackMove;

internal sealed class AccountRow
{
    public required string SystemId { get; init; }
    public string? Username { get; init; }
    public string? Description { get; init; }
    public string? AvatarUrl { get; init; }
    public short? AvatarSource { get; init; }
    public string? DiscordId { get; init; }
    public string? Email { get; init; }
    public string? AppleId { get; init; }
    public string? GoogleId { get; init; }
    public string? LinkToken { get; init; }
    public long? LinkTokenExpiresAtUnixMs { get; init; }
    public long CreatedAtUnixMs { get; init; }
    public long UpdatedAtUnixMs { get; init; }
    public int? LifetimeAlterCount { get; init; }
    public int? LegacyPrimaryFront { get; init; }
    public short? PrimaryFrontAlter { get; set; }
    public short? LastProxyId { get; init; }
    public DiscordSettingsRow? DiscordSettings { get; init; }
    public List<SettingsFieldRow> Fields { get; init; } = [];
    public EncryptionRow? Encryption { get; set; }
}

internal sealed class DiscordSettingsRow
{
    public string? SystemTag { get; init; }
    public bool ShowSystemTag { get; init; }
    public bool CaseInsensitiveProxies { get; init; }
    public bool ShowPronouns { get; init; }
    public bool IdsAsProxies { get; init; }
    public bool SilentProxying { get; init; }
    public bool UseProxyDelay { get; init; }
    public short GlobalAutoproxyMode { get; init; }
    public int? GlobalLatchedAlter { get; init; }
    public List<DiscordServerSettingsRow> ServerSettings { get; init; } = [];
}

internal sealed class DiscordServerSettingsRow
{
    public string? GuildId { get; init; }
    public bool ProxyingDisabled { get; init; }
    public short AutoproxyMode { get; init; }
    public int? LatchedAlter { get; init; }
}

internal sealed class EncryptionRow
{
    public bool Initialized { get; init; }
    public string? KeyChecksum { get; init; }
    public string? Salt { get; init; }
    public long UpdatedAtUnixMs { get; init; }
}

internal sealed class SettingsFieldRow
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public short Type { get; init; }
    public short SecurityLevel { get; init; }
    public bool Locked { get; init; }
    public int Index { get; init; }
    public long? InsertedAtUnixMs { get; init; }
    public long? UpdatedAtUnixMs { get; init; }
}

internal sealed class AlterRow
{
    public required string SystemId { get; init; }
    public short Id { get; init; }
    public string? Alias { get; init; }
    public required string Name { get; init; }
    public string? Pronouns { get; init; }
    public string? Description { get; init; }
    public string? AvatarUrl { get; init; }
    public short? AvatarSource { get; init; }
    public short SecurityLevel { get; init; }
    public List<string> ExtraImages { get; init; } = [];
    public string? Color { get; init; }
    public List<string> DiscordProxies { get; init; } = [];
    public string? ProxyName { get; init; }
    public bool Untracked { get; init; }
    public bool Archived { get; init; }
    public bool Pinned { get; init; }
    public long? LastFrontedUnixMs { get; init; }
    public long InsertedAtUnixMs { get; init; }
    public long UpdatedAtUnixMs { get; init; }
    public List<AlterFieldRow> Fields { get; init; } = [];
}

internal sealed class AlterFieldRow
{
    public required Guid Id { get; init; }
    public string? Value { get; init; }
}

internal sealed class TagRow
{
    public required string SystemId { get; init; }
    public required Guid Id { get; init; }
    public Guid? ParentTagId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Color { get; init; }
    public short SecurityLevel { get; init; }
    public long InsertedAtUnixMs { get; init; }
    public long UpdatedAtUnixMs { get; init; }
}

internal sealed class AlterTagRow
{
    public required string SystemId { get; init; }
    public required Guid TagId { get; init; }
    public short AlterId { get; init; }
    public long InsertedAtUnixMs { get; init; }
    public long UpdatedAtUnixMs { get; init; }
}

internal sealed class FriendshipRow
{
    public required string UserId { get; init; }
    public required string FriendId { get; init; }
    public short Level { get; init; }
    public long SinceUnixMs { get; init; }
}

internal sealed class FriendRequestRow
{
    public required string FromUserId { get; init; }
    public required string ToUserId { get; init; }
    public long DateSentUnixMs { get; init; }
}

internal sealed class NotificationTokenRow
{
    public required string SystemId { get; init; }
    public required string PushToken { get; init; }
    public long InsertedAtUnixMs { get; init; }
    public long UpdatedAtUnixMs { get; init; }
}

internal sealed class FrontRow
{
    public required string SystemId { get; init; }
    public required Guid Id { get; init; }
    public short AlterId { get; init; }
    public string? Comment { get; init; }
    public long TimeStartUnixMs { get; init; }
    public long? TimeEndUnixMs { get; init; }
    public bool Current { get; init; }
}

internal sealed class JournalRow
{
    public required string SystemId { get; init; }
    public required Guid Id { get; init; }
    public short? AlterId { get; init; }
    public required string Title { get; init; }
    public string? Content { get; init; }
    public string? Color { get; init; }
    public bool Pinned { get; init; }
    public bool Locked { get; init; }
    public long InsertedAtUnixMs { get; init; }
    public long UpdatedAtUnixMs { get; init; }
    public List<short> AlterIds { get; init; } = [];
}

internal sealed class PollRow
{
    public required string SystemId { get; init; }
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public short Type { get; init; }
    public string Data { get; init; } = "{}";
    public long? TimeEndUnixMs { get; init; }
    public long InsertedAtUnixMs { get; init; }
    public long UpdatedAtUnixMs { get; init; }
}

internal sealed class SecretRow
{
    public required string Key { get; init; }
    public required string Value { get; init; }
    public string CreatedBy { get; init; } = "stack-move";
    public long CreatedAtUnixMs { get; init; }
    public long UpdatedAtUnixMs { get; init; }
    public long? ExpiresAtUnixMs { get; init; }
    public string? RotatedFrom { get; init; }
}

internal sealed class StackSnapshot
{
    public List<AccountRow> Accounts { get; init; } = [];
    public List<AlterRow> Alters { get; init; } = [];
    public List<TagRow> Tags { get; init; } = [];
    public List<AlterTagRow> AlterTags { get; init; } = [];
    public List<FriendshipRow> Friendships { get; init; } = [];
    public List<FriendRequestRow> FriendRequests { get; init; } = [];
    public List<NotificationTokenRow> NotificationTokens { get; init; } = [];
    public List<FrontRow> Fronts { get; init; } = [];
    public List<JournalRow> Journals { get; init; } = [];
    public List<PollRow> Polls { get; init; } = [];
    public List<SecretRow> Secrets { get; init; } = [];
    public List<string> Regions { get; init; } = [];
}

internal sealed record MoveCounts(
    int Accounts,
    int Alters,
    int Tags,
    int Friendships,
    int FriendRequests,
    int NotificationTokens,
    int Fronts,
    int Journals,
    int Polls,
    int Secrets)
{
    public static MoveCounts From(StackSnapshot snapshot, int secrets) => new(
        snapshot.Accounts.Count,
        snapshot.Alters.Count,
        snapshot.Tags.Count,
        snapshot.Friendships.Count,
        snapshot.FriendRequests.Count,
        snapshot.NotificationTokens.Count,
        snapshot.Fronts.Count,
        snapshot.Journals.Count,
        snapshot.Polls.Count,
        secrets);
}
