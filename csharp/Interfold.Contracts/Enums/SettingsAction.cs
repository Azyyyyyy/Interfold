using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Outcome verb carried on <c>SettingsCommandResult.Action</c>. Serialized into persisted
/// idempotency outcome payloads and HTTP response bodies; the snake-case-lower wire spellings
/// ("description_updated", "avatar_uploaded", …) are frozen.
/// </summary>
[JsonConverter(typeof(LowerCaseEnumJsonConverter<SettingsAction>))]
public enum SettingsAction
{
    DescriptionUpdated,
    EncryptionReset,
    PushTokenAdded,
    PushTokenRemoved,
    AvatarUploaded,
    AvatarDeleted,
    AccountDeleted,
    TagsWiped,
    AltersWiped,
    EmailUnlinked,
    AppleUnlinked,
    DiscordUnlinked,
    FieldUpdated,
    FieldRelocated,
    FieldDeleted,
}
