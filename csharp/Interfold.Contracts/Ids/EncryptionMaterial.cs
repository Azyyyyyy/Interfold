using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// The derived per-system encryption key returned by the setup/recover encryption commands
/// (<c>EncryptionCommandResult.Key</c>). Secret material — <c>ToString()</c> redacts; the
/// raw-string converter keeps the persisted command-result JSON and replay hashes identical.
/// </summary>
[JsonConverter(typeof(EncryptionKeyMaterialJsonConverter))]
public readonly record struct EncryptionKeyMaterial
{
    public string Value { get; }

    public EncryptionKeyMaterial(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public override string ToString() => SecretRedaction.Redact(Value);
}

internal sealed class EncryptionKeyMaterialJsonConverter : JsonConverter<EncryptionKeyMaterial>
{
    public override EncryptionKeyMaterial Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, EncryptionKeyMaterial value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

/// <summary>
/// Checksum of the derived encryption key persisted to <c>encryption_state.key_checksum</c>
/// and compared on recover/import. <c>ToString()</c> redacts.
/// </summary>
public readonly record struct KeyChecksum
{
    public string Value { get; }

    public KeyChecksum(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public override string ToString() => SecretRedaction.Redact(Value);

    /// <summary>Null-preserving wrap for DB/state reads.</summary>
    public static KeyChecksum? FromNullable(string? value) => value is null ? null : new KeyChecksum(value);
}

/// <summary>
/// Per-system key-derivation salt persisted to <c>encryption_state.salt</c>. Not secret in
/// the cryptographic sense, but redacted anyway so state dumps don't hand out derivation
/// inputs alongside checksums.
/// </summary>
public readonly record struct EncryptionSalt
{
    public string Value { get; }

    public EncryptionSalt(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public override string ToString() => SecretRedaction.Redact(Value);

    /// <summary>Null-preserving wrap for DB/state reads.</summary>
    public static EncryptionSalt? FromNullable(string? value) => value is null ? null : new EncryptionSalt(value);
}
