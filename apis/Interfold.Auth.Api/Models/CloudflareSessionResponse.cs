using System.Text.Json.Serialization;

namespace Interfold.Auth.Api.Models;

/// <summary>Interfold session minted from a validated Cloudflare Access JWT.</summary>
public sealed record CloudflareSessionResponse
{
    /// <summary>Interfold ES256 JWT for subsequent API and socket calls.</summary>
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;

    /// <summary>Scoped system id (<c>region:rawId</c>) matching the JWT <c>sub</c>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
}
