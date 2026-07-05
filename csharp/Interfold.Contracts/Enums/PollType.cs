using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Poll type. Wire representation is the lowercase name (<c>"vote"</c>, <c>"choice"</c>,
/// <c>"approval"</c>), preserving compatibility with the pre-existing Elixir payloads
/// and the Kotlin client.
///
/// <para>
/// The Scylla schema stores this as a <c>smallint</c>: <c>0 → vote</c>, <c>1 → choice</c>,
/// <c>2 → approval</c>. See <see cref="PollTypeExtensions"/> for the mapping.
/// </para>
/// </summary>
[JsonConverter(typeof(LowerCaseEnumJsonConverter<PollType>))]
public enum PollType
{
    Vote = 0,
    Choice = 1,
    Approval = 2,
}

public static class PollTypeExtensions
{
    public static string ToWireValue(this PollType type) => type switch
    {
        PollType.Vote => "vote",
        PollType.Choice => "choice",
        PollType.Approval => "approval",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>
    /// Best-effort parse. Unknown values fall back to <see cref="PollType.Vote"/>, matching
    /// the Scylla mapping which returns "vote" for any code outside the 0-2 range.
    /// </summary>
    public static PollType ParseWireValueOrVote(string? value) => value switch
    {
        "vote" => PollType.Vote,
        "choice" => PollType.Choice,
        "approval" => PollType.Approval,
        _ => PollType.Vote,
    };

    public static PollType FromCode(short code) => code switch
    {
        0 => PollType.Vote,
        1 => PollType.Choice,
        2 => PollType.Approval,
        _ => PollType.Vote,
    };

    public static short ToCode(this PollType type) => (short)type;
}
