using Interfold.Shared.Contracts.Ids;

namespace Interfold.Infrastructure.Sqlite;

// Dapper.AOT row factories emit `new T()` plus property sets. Positional records
// have no parameterless ctor, so Query<T> falls through to Reflection.Emit.
// SystemId columns stay SystemId via SystemIdTypeHandler. Scalar
// QueryFirstOrDefaultAsync<SystemId?> is not intercepted — use SystemIdRow,
// and keep SQL a compile-time constant at the Query call.
internal sealed class AccountRow
{
    public string? Username { get; set; }
    public string? Description { get; set; }
    public string? AvatarUrl { get; set; }
    public long? AvatarSource { get; set; }
    public string? DiscordId { get; set; }
    public string? Email { get; set; }
    public string? AppleId { get; set; }
}

internal sealed class LinkTokenRow
{
    public string? LinkToken { get; set; }
    // see SqliteAccountRepository COALESCE; Dapper.AOT GetInt64 refuses NULL after scrub
    public long ExpiresAt { get; set; }
}

internal sealed class LinkTokenOwnerRow
{
    public SystemId SystemId { get; set; }
    public long ExpiresAt { get; set; }
}

internal sealed class SystemIdRow
{
    public SystemId SystemId { get; set; }

    public SystemId? OrNull()
        => string.IsNullOrWhiteSpace(SystemId.Value) ? null : SystemId;
}

internal sealed class EncryptionStateRow
{
    public long Initialized { get; set; }
    public string? Checksum { get; set; }
    public string? Salt { get; set; }
}

internal sealed class IdempotencyRow
{
    public string PayloadHash { get; set; } = "";
    public string OutcomeHash { get; set; } = "";
    public string? OutcomePayload { get; set; }
}

internal sealed class SecretRow
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public long? ExpiresAt { get; set; }
    public string? RotatedFrom { get; set; }
}

internal sealed class FriendshipRow
{
    public SystemId FriendId { get; set; }
    public long Level { get; set; }
    public long Since { get; set; }
}

internal sealed class FriendRequestRow
{
    public SystemId OtherUserId { get; set; }
    public long DateSent { get; set; }
}

internal sealed class AlterRowDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Alias { get; set; }
    public string? Description { get; set; }
    public string? AvatarUrl { get; set; }
    public long? AvatarSource { get; set; }
    public string? Color { get; set; }
    public string? Pronouns { get; set; }
    public long SecurityLevel { get; set; }
    public string? ProxyName { get; set; }
    public long Untracked { get; set; }
    public long Archived { get; set; }
    public long Pinned { get; set; }
    public long InsertedAt { get; set; }
    public long UpdatedAt { get; set; }
}

internal sealed class AlterFieldRow
{
    public long AlterId { get; set; }
    public string FieldId { get; set; } = "";
    public string? Value { get; set; }
}

internal sealed class AlterFieldValueRow
{
    public string FieldId { get; set; } = "";
    public string? Value { get; set; }
}

internal sealed class FieldRow
{
    public string IdHex { get; set; } = "";
    public string Name { get; set; } = "";
    public long Type { get; set; }
    public long SecurityLevel { get; set; }
    public long Locked { get; set; }
    public long Idx { get; set; }
    public long InsertedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }
}

internal sealed class TagRowDto
{
    public string IdHex { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public string? Description { get; set; }
    public string? ParentTagIdHex { get; set; }
    public long SecurityLevel { get; set; }
    public long InsertedAt { get; set; }
    public long UpdatedAt { get; set; }
}

internal sealed class AlterTagRow
{
    public string TagIdHex { get; set; } = "";
    public long AlterId { get; set; }
}

internal sealed class PollRow
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public long Type { get; set; }
    public string? Data { get; set; }
    public long? TimeEnd { get; set; }
    public long InsertedAt { get; set; }
    public long UpdatedAt { get; set; }
}

internal sealed class CurrentFrontRow
{
    public string Id { get; set; } = "";
    public long AlterId { get; set; }
    public string? Comment { get; set; }
    public long TimeStart { get; set; }
}

internal sealed class ActiveFrontByIdRow
{
    public long AlterId { get; set; }
    public string? Comment { get; set; }
    public long TimeStart { get; set; }
}

internal sealed class FrontHistoryRow
{
    public string Id { get; set; } = "";
    public long AlterId { get; set; }
    public string? Comment { get; set; }
    public long TimeStart { get; set; }
    public long? TimeEnd { get; set; }
}

internal sealed class ImportOperationRow
{
    public SystemId SystemId { get; set; }
    public string OperationId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "";
    public long StartedAt { get; set; }
    public long? FinishedAt { get; set; }
    public long? AlterCount { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string IdempotencyKey { get; set; } = "";
}

internal sealed class AlterJournalRefRow
{
    public string Id { get; set; } = "";
    public long AlterId { get; set; }
}

internal sealed class GlobalJournalRow
{
    public string Id { get; set; } = "";
    public SystemId UserId { get; set; }
    public string Title { get; set; } = "";
    public string? Content { get; set; }
    public string? Color { get; set; }
    public long Pinned { get; set; }
    public long Locked { get; set; }
    public long InsertedAt { get; set; }
    public long UpdatedAt { get; set; }
}

internal sealed class AlterJournalRow
{
    public string Id { get; set; } = "";
    public SystemId UserId { get; set; }
    public long AlterId { get; set; }
    public string Title { get; set; } = "";
    public string? Content { get; set; }
    public string? Color { get; set; }
    public long Pinned { get; set; }
    public long Locked { get; set; }
    public long InsertedAt { get; set; }
    public long UpdatedAt { get; set; }
}
