using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;

namespace Interfold.Bootstrapper.Util;

/// <summary>Stages Let's Encrypt credentials + a bootstrap self-signed cert so edge-nginx
/// can start before the first certbot run replaces it with a real LE certificate.</summary>
internal static class LetsEncryptStaging
{
    internal static void EnsureCredentialsAndPlaceholderCerts(
        BootstrapConfig config, string outputDir, PhaseLogger logger)
    {
        var leRoot = Path.Combine(outputDir, "certs", "letsencrypt");
        Directory.CreateDirectory(leRoot);
        Directory.CreateDirectory(Path.Combine(outputDir, "certs", "certbot-www"));

        if (config.Edge.Cloudflare.IpAllowlist
            && !string.IsNullOrWhiteSpace(config.Edge.Cloudflare.DnsApiToken))
        {
            var credPath = Path.Combine(leRoot, "cloudflare.ini");
            var body =
                $"dns_cloudflare_api_token = {config.Edge.Cloudflare.DnsApiToken.Trim()}\n";
            File.WriteAllText(credPath, body);
            TryRestrictToOwner(credPath);
            logger.Info($"    wrote Cloudflare DNS-01 credentials → {credPath}");
        }

        var domain = ResolvePrimaryDomain(config);
        if (string.IsNullOrWhiteSpace(domain) || domain == "_")
        {
            logger.Warn("    Let's Encrypt: no DNS primary host; skipping placeholder cert");
            return;
        }

        var liveDir = Path.Combine(leRoot, "live", domain);
        var fullchain = Path.Combine(liveDir, "fullchain.pem");
        var privkey = Path.Combine(liveDir, "privkey.pem");
        if (File.Exists(fullchain) && File.Exists(privkey))
            return;

        Directory.CreateDirectory(liveDir);
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={domain}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        File.WriteAllText(fullchain, PemEncoding.WriteString("CERTIFICATE", cert.RawData) + "\n");
        File.WriteAllText(privkey, PemEncoding.WriteString("PRIVATE KEY", rsa.ExportPkcs8PrivateKey()) + "\n");
        TryRestrictToOwner(privkey);
        logger.Info(
            $"    wrote temporary self-signed placeholder under {liveDir} " +
            "(replace with certbot; see docs/configuration.md)");

        WriteObtainHint(leRoot, config, domain, logger);
    }

    private static void WriteObtainHint(
        string leRoot, BootstrapConfig config, string domain, PhaseLogger logger)
    {
        var hintPath = Path.Combine(leRoot, "OBTAIN.md");
        if (File.Exists(hintPath)) return;

        var dns01 = config.Edge.Cloudflare.IpAllowlist;
        var body = dns01
            ? $"""
              # Obtain a Let's Encrypt certificate (Cloudflare DNS-01)

              docker run --rm \
                -v "{leRoot}:/etc/letsencrypt" \
                -v "{Path.Combine(Path.GetDirectoryName(leRoot)!, "certbot-www")}:/var/www/certbot" \
                -v "{Path.Combine(leRoot, "cloudflare.ini")}:/cloudflare.ini:ro" \
                certbot/dns-cloudflare certonly \
                --dns-cloudflare --dns-cloudflare-credentials /cloudflare.ini \
                -d {domain} --agree-tos --non-interactive -m admin@{domain}

              Then reload edge-nginx: `docker compose exec edge-nginx nginx -s reload`
              """
            : $"""
              # Obtain a Let's Encrypt certificate (HTTP-01)

              Ensure edge.ports.http=80 is reachable, then:

              docker run --rm \
                -v "{leRoot}:/etc/letsencrypt" \
                -v "{Path.Combine(Path.GetDirectoryName(leRoot)!, "certbot-www")}:/var/www/certbot" \
                certbot/certbot certonly --webroot -w /var/www/certbot \
                -d {domain} --agree-tos --non-interactive -m admin@{domain}

              Then reload edge-nginx: `docker compose exec edge-nginx nginx -s reload`
              """;
        File.WriteAllText(hintPath, body.Replace("\r\n", "\n"));
        logger.Info($"    wrote certbot instructions → {hintPath}");
    }

    private static string ResolvePrimaryDomain(BootstrapConfig config)
    {
        if (config.Edge.Routing.Mode == EdgeRoutingMode.Subdomain
            && !string.IsNullOrWhiteSpace(config.Edge.Routing.ApiHost))
        {
            return config.Edge.Routing.ApiHost.Trim();
        }

        foreach (var raw in config.Edge.Hosts)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var entry = HostParser.Parse(raw);
                if (entry.Kind == HostKind.Dns) return entry.DnsName!;
            }
            catch (FormatException)
            {
            }
        }

        return "_";
    }

    private static void TryRestrictToOwner(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // best-effort; Windows/dev hosts ignore
        }
    }
}
