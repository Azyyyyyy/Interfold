using System.Text.Json.Serialization;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>How <c>edge-nginx</c> terminates TLS (always emitted).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EdgeTlsMode>))]
public enum EdgeTlsMode
{
    /// <summary>Plaintext :80 only; no TLS server block or HTTP→HTTPS redirect.</summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>Bootstrapper private CA leaf under <c>{outputDir}/certs/leaf.*</c>.</summary>
    [JsonStringEnumMemberName("privateCa")]
    PrivateCa,

    /// <summary>Let's Encrypt via certbot (DNS-01 when Cloudflare allowlist is on).</summary>
    [JsonStringEnumMemberName("letsEncrypt")]
    LetsEncrypt,
}
