using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Poll type. Wire representation is the lowercase name (<c>"vote"</c>, <c>"choice"</c>,
/// <c>"approval"</c>), preserving compatibility with the pre-existing Elixir payloads
/// and the Kotlin client.
///
/// <para>
/// The Scylla schema stores this as a <c>smallint</c>: <c>0 → vote</c>, <c>1 → choice</c>,
/// <c>2 → approval</c>. Callers cast <c>(short)value</c> outbound and use
/// <c>EnumCode&lt;PollType&gt;.FromCode(code, PollType.Vote)</c> inbound; unknown codes fall
/// back to <c>Vote</c> to match the historical row-mapping behaviour.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PollType>))]
public enum PollType : short
{
    [JsonStringEnumMemberName("vote")]
    Vote = 0,

    [JsonStringEnumMemberName("choice")]
    Choice = 1,

    [JsonStringEnumMemberName("approval")]
    Approval = 2,
}

public static class PollTypeExtensions
{
    /// <summary>
    /// Best-effort case-insensitive parse. Unknown values fall back to <see cref="PollType.Vote"/>,
    /// matching the Scylla mapping which returns "vote" for any code outside the 0-2 range.
    /// </summary>
    public static PollType ParseWireValueOrVote(string? value)
        => value.TryParseWire<PollType>(out var type) ? type : PollType.Vote;
}
