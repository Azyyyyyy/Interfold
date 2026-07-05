using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Outcome verb carried on <c>EncryptionCommandResult.Action</c>. Serialized into persisted
/// idempotency outcome payloads and HTTP response bodies; wire spellings
/// ("encryption_setup" / "encryption_recovered") are frozen.
/// </summary>
[JsonConverter(typeof(LowerCaseEnumJsonConverter<EncryptionAction>))]
public enum EncryptionAction
{
    EncryptionSetup,
    EncryptionRecovered,
}
