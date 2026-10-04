using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Enums;

/// <summary>
/// Autoproxy mode. Wire representation is the lowercase name; the persistence schema stores
/// this as a <c>smallint</c>. Server currently hard-codes <see cref="Off"/> on the read-model
/// boundary — reserved for future wiring of latch/front autoproxy.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AutoproxyMode>))]
public enum AutoproxyMode : short
{
    [JsonStringEnumMemberName("off")]
    Off = 0,

    [JsonStringEnumMemberName("latch")]
    Latch = 1,

    [JsonStringEnumMemberName("front")]
    Front = 2,
}
