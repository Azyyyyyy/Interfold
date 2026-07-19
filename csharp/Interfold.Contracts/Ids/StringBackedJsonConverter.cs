using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// Common JSON converter shape for the ~11 string-backed wrapper structs that live outside
/// the two existing family-scoped bases (<see cref="SecretStringJsonConverter{T}"/> for the
/// six PII/secret tokens; <see cref="GuidIdJsonConverter{T}"/> for the five Guid entity IDs).
/// This one covers the "plain public string wrapper" shape used by <c>SystemId</c>,
/// <c>DiscordId</c>, <c>Email</c>, <c>AppleId</c>, <c>AvatarUrl</c>,
/// <c>EncryptionKeyMaterial</c>, <c>EntityRef</c>, <c>IdempotencyKey</c>,
/// <c>OperationId</c>, <c>ErrorCode</c>, and <c>PollChoiceId</c>.
///
/// <para>
/// Read is null-tolerant (<c>null</c> becomes <c>string.Empty</c>) rather than throwing,
/// matching the behaviour of the ~11 hand-rolled bodies this base replaces. Individual
/// subclasses that need different behaviour (e.g. <c>UsernameJsonConverter</c>, which sets
/// <c>HandleNull = true</c> and inspects <c>TokenType == Null</c>) stay hand-rolled from
/// <see cref="JsonConverter{T}"/> directly — the same pattern the sibling bases use
/// (<see cref="SecretStringJsonConverter{T}"/> and <see cref="GuidIdJsonConverter{T}"/>
/// both <c>sealed override</c> their <c>Read</c>/<c>Write</c>).
/// </para>
///
/// <para>
/// Concrete stubs stay <c>sealed</c> and are named individually because
/// <c>[JsonConverter(typeof(...))]</c> attribute application requires a concrete class name.
/// </para>
///
/// <para>
/// <b>Naming:</b> deliberately not "StringWrapperJsonConverter" — <c>SecretStringJsonConverter&lt;T&gt;</c>
/// already occupies the "secret" spelling for the redacted-toString tokens, and this base is
/// the non-secret counterpart. The three bases are siblings, one per wrapper family.
/// </para>
/// </summary>
internal abstract class StringBackedJsonConverter<T> : JsonConverter<T>
{
    protected abstract T Create(string value);
    protected abstract string GetValue(T value);

    public sealed override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Create(reader.GetString() ?? string.Empty);

    public sealed override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(GetValue(value));
}
