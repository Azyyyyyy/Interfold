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

    /// <summary>Run-mode bind-mount source. Publish stages a copy as
    /// <c>default.conf.template</c>; <c>aspire run</c> has no staging step.</summary>
    public static string SourceTemplate(string repoRoot, bool subdomain, bool plaintext)
    {
        var file = (subdomain, plaintext) switch
        {
            (true, true) => "subdomain-http.conf.template",
            (true, false) => "subdomain.conf.template",
            (false, true) => "path-http.conf.template",
            (false, false) => "path.conf.template",
        };
        return Path.GetFullPath(Path.Combine(repoRoot, "support", "edge", "nginx", file));
    }

    public static string SourceProxyParams(string repoRoot) =>
        Path.GetFullPath(Path.Combine(repoRoot, "support", "edge", "nginx", "proxy_params.conf"));
}
