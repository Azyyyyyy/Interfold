using System.Text.Json.Serialization;

namespace Interfold.Bootstrapper.Util;

internal sealed class DiscordOidcConfigJson
{
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("clientSecret")]
    public string ClientSecret { get; set; } = string.Empty;

    [JsonPropertyName("redirectURL")]
    public string RedirectUrl { get; set; } = string.Empty;

    [JsonPropertyName("serversToCheckRolesFor")]
    public List<string> ServersToCheckRolesFor { get; set; } = [];
}

internal sealed class DiscordOidcPackageJson
{
    [JsonPropertyName("dependencies")]
    public Dictionary<string, string> Dependencies { get; set; } = new(StringComparer.Ordinal);
}
