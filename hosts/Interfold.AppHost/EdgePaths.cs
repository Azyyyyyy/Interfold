namespace Interfold.AppHost;

/// <summary>Filesystem wire contract for edge-nginx bind mounts. Host paths are relative
/// to the emitted compose file (same convention as <see cref="CertsPaths"/>).</summary>
public static class EdgePaths
{
    public const string HostSupportDir = "../../support/edge/nginx";
    public const string HostCertsDir = "../../certs";

    public const string ContainerNginxTemplate = "/etc/nginx/templates/default.conf.template";
    public const string ContainerProxyParams = "/etc/nginx/proxy_params_interfold.conf";
    public const string ContainerCertsDir = "/certs";

    public const string LeafCrt = "/certs/leaf.crt";
    public const string LeafKey = "/certs/leaf.key";

    public const string ContainerCloudflareTunnelToken = "/run/secrets/cloudflare-tunnel.token";
}
