using System.Text.Json.Serialization;

namespace Interfold.Auth.Api.Models;

/// <summary>Anonymous discovery of which login buttons the client should show.</summary>
public sealed record LoginMethodsResponse
{
    /// <summary>When true, show only Cloudflare Access; hide Discord/Apple/Google Interfold buttons.</summary>
    [JsonPropertyName("cloudflare")]
    public bool Cloudflare { get; init; }

    /// <summary>Interfold Google OAuth is configured (client id present).</summary>
    [JsonPropertyName("google")]
    public bool Google { get; init; }

    /// <summary>Interfold Discord OAuth is configured (client id present).</summary>
    [JsonPropertyName("discord")]
    public bool Discord { get; init; }

    /// <summary>Interfold Apple OAuth is configured (client id present).</summary>
    [JsonPropertyName("apple")]
    public bool Apple { get; init; }
}
