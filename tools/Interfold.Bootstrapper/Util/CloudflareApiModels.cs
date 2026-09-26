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
    // JsonElement? AV'd STJ source-gen Create_* under Linux Release; Cloudflare codes are ints.
    [JsonPropertyName("code")]
    public int? Code { get; set; }

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

    [JsonPropertyName("name_servers")]
    public List<string> NameServers { get; set; } = [];
}

internal sealed class CloudflareAccountResult
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class CloudflareCreateZoneRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("account")]
    public CloudflareAccountRef Account { get; set; } = new();

    [JsonPropertyName("type")]
    public string Type { get; set; } = "full";

    // Default true would import the registrar's current records into the new zone.
    [JsonPropertyName("jump_start")]
    public bool JumpStart { get; set; }
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

internal sealed class CloudflareOidcIdpRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "oidc";

    [JsonPropertyName("config")]
    public CloudflareOidcIdpConfig Config { get; set; } = new();
}

internal sealed class CloudflareOidcIdpConfig
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("client_secret")]
    public string ClientSecret { get; set; } = string.Empty;

    [JsonPropertyName("auth_url")]
    public string AuthUrl { get; set; } = string.Empty;

    [JsonPropertyName("token_url")]
    public string TokenUrl { get; set; } = string.Empty;

    [JsonPropertyName("certs_url")]
    public string CertsUrl { get; set; } = string.Empty;

    [JsonPropertyName("pkce_enabled")]
    public bool PkceEnabled { get; set; }

    [JsonPropertyName("email_claim_name")]
    public string EmailClaimName { get; set; } = "email";

    [JsonPropertyName("claims")]
    public List<string> Claims { get; set; } = [];

    [JsonPropertyName("scopes")]
    public List<string> Scopes { get; set; } = [];
}

internal sealed class CloudflareKvNamespaceRequest
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
}

internal sealed class CloudflareKvNamespaceResult
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

internal sealed class CloudflareWorkersSubdomainResult
{
    [JsonPropertyName("subdomain")]
    public string? Subdomain { get; set; }
}

internal sealed class CloudflareWorkersScriptSubdomainRequest
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

internal sealed class CloudflareWorkerUploadMetadata
{
    [JsonPropertyName("main_module")]
    public string MainModule { get; set; } = "worker.js";

    [JsonPropertyName("compatibility_date")]
    public string CompatibilityDate { get; set; } = "2022-12-24";

    [JsonPropertyName("bindings")]
    public List<CloudflareWorkerBinding> Bindings { get; set; } = [];
}

internal sealed class CloudflareWorkerBinding
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("namespace_id")]
    public string? NamespaceId { get; set; }
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

    // Browser preflight omits cookies, so Access would 403 OPTIONS before the API's CORS policy runs.
    [JsonPropertyName("options_preflight_bypass")]
    public bool OptionsPreflightBypass { get; set; }
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

internal sealed class CloudflareTotalTlsSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("certificate_authority")]
    public string? CertificateAuthority { get; set; }
}

internal sealed class CloudflareCertificatePackOrderRequest
{
    [JsonPropertyName("certificate_authority")]
    public string CertificateAuthority { get; set; } = "lets_encrypt";

    [JsonPropertyName("hosts")]
    public List<string> Hosts { get; set; } = [];

    [JsonPropertyName("type")]
    public string Type { get; set; } = "advanced";

    [JsonPropertyName("validation_method")]
    public string ValidationMethod { get; set; } = "txt";

    [JsonPropertyName("validity_days")]
    public int ValidityDays { get; set; } = 90;

    [JsonPropertyName("cloudflare_branding")]
    public bool CloudflareBranding { get; set; }
}

internal sealed class CloudflareCertificatePackResult
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("hosts")]
    public List<string> Hosts { get; set; } = [];

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}
