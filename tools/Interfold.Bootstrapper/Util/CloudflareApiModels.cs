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

internal sealed class CloudflareAccessOrganizationResult
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("auth_domain")]
    public string? AuthDomain { get; set; }
}

internal sealed class CloudflareGoogleIdpRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "google";

    [JsonPropertyName("config")]
    public CloudflareGoogleIdpConfig Config { get; set; } = new();
}

internal sealed class CloudflareGoogleIdpConfig
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("client_secret")]
    public string ClientSecret { get; set; } = string.Empty;
}

internal sealed class CloudflareIdentityProviderResult
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class CloudflareSelfHostedAppRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "self_hosted";

    [JsonPropertyName("session_duration")]
    public string SessionDuration { get; set; } = "24h";

    [JsonPropertyName("auto_redirect_to_identity")]
    public bool AutoRedirectToIdentity { get; set; }

    [JsonPropertyName("allowed_idps")]
    public List<string> AllowedIdps { get; set; } = [];
}

internal sealed class CloudflareAccessAppResult
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("domain")]
    public string? Domain { get; set; }

    [JsonPropertyName("aud")]
    public string? Aud { get; set; }
}

internal sealed class CloudflareAccessPolicyRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("decision")]
    public string Decision { get; set; } = string.Empty;

    [JsonPropertyName("include")]
    public List<CloudflareAccessPolicyCondition> Include { get; set; } = [];

    [JsonPropertyName("require")]
    public List<CloudflareAccessPolicyCondition> Require { get; set; } = [];
}

[JsonConverter(typeof(CloudflareAccessPolicyConditionJsonConverter))]
internal sealed class CloudflareAccessPolicyCondition
{
    public CloudflareAccessPolicyRuleKind Kind { get; set; }
    public string Value { get; set; } = string.Empty;

    public static CloudflareAccessPolicyCondition From(CloudflareAccessPolicyRule rule)
        => new() { Kind = rule.Kind, Value = rule.Value };
}

internal sealed class CloudflareAccessPolicyResult
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal sealed class CloudflareServiceTokenRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

internal sealed class CloudflareServiceTokenResult
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    [JsonPropertyName("client_secret")]
    public string? ClientSecret { get; set; }
}

internal sealed class CloudflareAccessPolicyConditionJsonConverter : JsonConverter<CloudflareAccessPolicyCondition>
{
    public override CloudflareAccessPolicyCondition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new JsonException("Access policy conditions are write-only.");

    public override void Write(Utf8JsonWriter writer, CloudflareAccessPolicyCondition value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value.Kind)
        {
            case CloudflareAccessPolicyRuleKind.Email:
                writer.WritePropertyName("email");
                writer.WriteStartObject();
                writer.WriteString("email", value.Value);
                writer.WriteEndObject();
                break;
            case CloudflareAccessPolicyRuleKind.EmailDomain:
                writer.WritePropertyName("email_domain");
                writer.WriteStartObject();
                writer.WriteString("domain", value.Value);
                writer.WriteEndObject();
                break;
            case CloudflareAccessPolicyRuleKind.LoginMethod:
                writer.WritePropertyName("login_method");
                writer.WriteStartObject();
                writer.WriteString("id", value.Value);
                writer.WriteEndObject();
                break;
            case CloudflareAccessPolicyRuleKind.Everyone:
                writer.WritePropertyName("everyone");
                writer.WriteStartObject();
                writer.WriteEndObject();
                break;
            case CloudflareAccessPolicyRuleKind.ServiceToken:
                writer.WritePropertyName("service_token");
                writer.WriteStartObject();
                writer.WriteString("token_id", value.Value);
                writer.WriteEndObject();
                break;
            default:
                throw new JsonException($"Unsupported Access policy rule kind '{value.Kind}'.");
        }

        writer.WriteEndObject();
    }
}
