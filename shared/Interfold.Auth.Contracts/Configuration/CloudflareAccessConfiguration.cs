namespace Interfold.Auth.Contracts.Configuration;

/// <summary>Cloudflare Access JWT exchange. Empty team domain or AUD disables
/// <c>/auth/cloudflare</c>.</summary>
public sealed class CloudflareAccessConfiguration
{
    public string TeamDomain { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(TeamDomain) && !string.IsNullOrWhiteSpace(Audience);
}
