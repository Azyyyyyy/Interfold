namespace Interfold.Shared.Contracts.Configuration;

/// <summary>Docker Compose service names emitted by <c>InterfoldAppHost</c>. Shared with
/// the bootstrapper's ConfigPhase whitelist; any addition MUST also be added to
/// <c>InterfoldAppHost.Configure</c>.</summary>
public static class ComposeServices
{

    /// <summary>API container. Emits OCTOCON_* env, hosts the WebSocket endpoint.</summary>
    public const string InterfoldApi = "interfold-api";

    /// <summary>Static interfold-web wasm UI (HTTP). Public TLS is terminated by <see cref="EdgeNginx"/> when enabled.</summary>
    public const string InterfoldWeb = "interfold-web";

    /// <summary>Public edge reverse proxy for HTTP/HTTPS in front of API (+ web).</summary>
    public const string EdgeNginx = "edge-nginx";

    /// <summary>Cloudflare Tunnel connector (<c>cloudflared</c>) when <c>edge.cloudflare.enabled</c>.</summary>
    public const string Cloudflared = "cloudflared";

    /// <summary>All compose services the bootstrapper will accept as an <c>--service</c> filter.</summary>
    public static readonly string[] AllValidUpdateServices =
    [
        InterfoldApi,
        InterfoldWeb,
        EdgeNginx,
        Cloudflared,
    ];

    /// <summary>Accepts <c>octocon-web</c> as <see cref="InterfoldWeb"/> in --service filters.</summary>
    public static string CanonicalizeUpdateService(string service)
        => string.Equals(service, "octocon-web", StringComparison.Ordinal)
            ? InterfoldWeb
            : service;
}
