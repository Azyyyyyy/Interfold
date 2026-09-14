using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Bootstrapper.Util;

internal sealed class CloudflareApiResponse<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errors")]
    public List<CloudflareApiError> Errors { get; set; } = [];

    [JsonPropertyName("result")]
    public T? Result { get; set; }
}

internal sealed class CloudflareApiError
{
    [JsonPropertyName("code")]
    public JsonElement? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal sealed class CloudflareZoneResult
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("account")]
    public CloudflareAccountRef? Account { get; set; }
}

internal sealed class CloudflareAccountRef
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal sealed class CloudflareCreateTunnelRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("config_src")]
    public string ConfigSrc { get; set; } = "cloudflare";
}

internal sealed class CloudflareTunnelResult
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("connections")]
    public List<CloudflareTunnelConnection> Connections { get; set; } = [];
}

internal sealed class CloudflareTunnelConnection;

internal sealed class CloudflarePutIngressRequest
{
    [JsonPropertyName("config")]
    public CloudflareTunnelConfig Config { get; set; } = new();
}

internal sealed class CloudflareTunnelConfig
{
    [JsonPropertyName("ingress")]
    public List<CloudflareIngressRule> Ingress { get; set; } = [];
}

internal sealed class CloudflareIngressRule
{
    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    [JsonPropertyName("service")]
    public string Service { get; set; } = string.Empty;

    [JsonPropertyName("originRequest")]
    public CloudflareOriginRequest? OriginRequest { get; set; }
}

internal sealed class CloudflareOriginRequest;

internal sealed class CloudflareDnsRecordRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "CNAME";

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("proxied")]
    public bool Proxied { get; set; }

    [JsonPropertyName("ttl")]
    public int Ttl { get; set; }
}

internal sealed class CloudflareDnsRecordResult
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}
