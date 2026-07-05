using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// The friend-request target handle: the route accepts either a 7-character system id or a
/// username, resolved to a <see cref="SystemId"/> by
/// <c>IFriendshipRepository.ResolveUserIdAsync</c>. Distinct from <see cref="SystemId"/> so
/// the "this string might be a username" fact is visible in the type system rather than
/// smuggled through an id type.
///
/// <para>
/// <b>Wire compatibility.</b> JSON serializes as the raw underlying string (the persisted
/// <c>SendFriendRequestCommand</c> payload keeps its historical shape and idempotency
/// hashes). Route binding via <see cref="IParsable{TSelf}"/>.
/// </para>
/// </summary>
[JsonConverter(typeof(UsernameOrSystemIdJsonConverter))]
public readonly record struct UsernameOrSystemId : IParsable<UsernameOrSystemId>
{
    public string Value { get; }

    public UsernameOrSystemId(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public override string ToString() => Value;

    public static UsernameOrSystemId Parse(string s, IFormatProvider? provider) => new(s);

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out UsernameOrSystemId result)
    {
        if (s is null)
        {
            result = default;
            return false;
        }

        result = new UsernameOrSystemId(s);
        return true;
    }
}

internal sealed class UsernameOrSystemIdJsonConverter : JsonConverter<UsernameOrSystemId>
{
    public override UsernameOrSystemId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, UsernameOrSystemId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
