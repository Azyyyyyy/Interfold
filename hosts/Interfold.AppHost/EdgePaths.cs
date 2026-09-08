namespace Interfold.AppHost;

/// <summary>Filesystem wire contract for edge-nginx bind mounts. Host paths are relative
/// to the emitted compose file (same convention as <see cref="CertsPaths"/>).</summary>
public static class EdgePaths
{
    public const string HostSupportDir = "../../support/edge/nginx";
    public const string HostCertsDir = "../../certs";
    public const string HostLetsEncryptDir = "../../certs/letsencrypt";
    public const string HostAcmeWebroot = "../../certs/certbot-www";

    public const string ContainerNginxTemplate = "/etc/nginx/templates/default.conf.template";
    public const string ContainerProxyParams = "/etc/nginx/proxy_params_interfold.conf";
    public const string ContainerCloudflareIps = "/etc/nginx/cloudflare-ips.conf";
    public const string ContainerCertsDir = "/certs";
    public const string ContainerAcmeWebroot = "/var/www/certbot";

    public const string LeafCrt = "/certs/leaf.crt";
    public const string LeafKey = "/certs/leaf.key";

    /// <summary>Let's Encrypt live material; domain directory is selected via env at runtime.</summary>
    public const string LetsEncryptLiveRoot = "/certs/letsencrypt/live";
}
