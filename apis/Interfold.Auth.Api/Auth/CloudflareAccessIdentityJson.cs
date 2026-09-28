using System.Text.Json.Serialization;

namespace Interfold.Auth.Api.Auth;

internal sealed class CloudflareAccessIdentityJson
{
    [JsonPropertyName("idp")]
    public CloudflareAccessIdpJson? Idp { get; set; }

    [JsonPropertyName("oidc_fields")]
    public CloudflareAccessOidcClaimsJson? OidcFields { get; set; }

    [JsonPropertyName("custom")]
    public CloudflareAccessOidcClaimsJson? Custom { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal sealed class CloudflareAccessIdpJson
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class CloudflareAccessOidcClaimsJson
{
    [JsonPropertyName("id")]
    public ulong? Id { get; set; }

    [JsonPropertyName("sub")]
    public ulong? Sub { get; set; }
}
