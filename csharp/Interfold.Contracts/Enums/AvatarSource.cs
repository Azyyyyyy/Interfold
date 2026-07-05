using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Discriminator for the hosting source of an avatar URL.
/// Stored as <c>smallint</c> in Scylla, exposed on the wire as snake-case strings
/// for parity with <see cref="Interfold.Contracts.Models.VisibilityLevel"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AvatarSource
{
    /// <summary>
    /// Avatar bytes are persisted by <c>IAvatarStorage</c> on this deployment;
    /// the stored URL is a relative path that requires origin qualification when
    /// returned to a client.
    /// </summary>
    [JsonStringEnumMemberName("local")]
    Local = 0,

    /// <summary>
    /// Avatar lives on a third-party host. The stored URL is absolute and is
    /// returned to the client unchanged; cleanup paths must not delete it.
    /// </summary>
    [JsonStringEnumMemberName("external")]
    External = 1,
}

public static class AvatarSourceExtensions
{
    public static short ToCode(this AvatarSource source) => (short)source;

    /// <summary>
    /// Maps the persisted <c>avatar_source</c> smallint into the enum. Returns
    /// <see langword="null"/> for null or unknown codes (no avatar set), so boundaries can
    /// omit <c>avatar_source</c> from the response payload.
    /// </summary>
    public static AvatarSource? TryFromCode(short? code) => code switch
    {
        (short)AvatarSource.Local => AvatarSource.Local,
        (short)AvatarSource.External => AvatarSource.External,
        _ => null,
    };
}
