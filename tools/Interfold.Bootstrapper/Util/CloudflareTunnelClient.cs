using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Interfold.Bootstrapper.Cli;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.Util;

/// <summary>Cloudflare REST client for remotely-managed tunnels (API token only — never the connector token).</summary>
internal sealed class CloudflareTunnelClient : IDisposable
{
    private readonly HttpClient _http;

    internal CloudflareTunnelClient(HttpClient http)
    {
        _http = http;
    }

    public void Dispose() => _http.Dispose();

    internal const string DefaultApiBaseUrl = "https://api.cloudflare.com/client/v4/";

    /// <summary>Optional override for DinD / unit tests (<c>INTERFOLD_CLOUDFLARE_API_BASE_URL</c>).</summary>
    internal static string ResolveApiBaseUrl()
    {
        var overrideBase = Environment.GetEnvironmentVariable("INTERFOLD_CLOUDFLARE_API_BASE_URL");
        if (string.IsNullOrWhiteSpace(overrideBase))
            return DefaultApiBaseUrl;

        var trimmed = overrideBase.Trim();
        return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }

    internal static CloudflareTunnelClient Create(string apiToken)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            BaseAddress = new Uri(ResolveApiBaseUrl()),
            Timeout = TimeSpan.FromMinutes(2),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
        http.DefaultRequestHeaders.UserAgent.ParseAdd(BootstrapperVersion.UserAgent);
        return new CloudflareTunnelClient(http);
    }

    internal async Task<CloudflareZoneAccount> ResolveZoneAndAccountAsync(string hostname, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in EnumerateZoneCandidates(hostname))
        {
            if (!seen.Add(candidate))
                continue;

            var existing = await FindZoneByNameAsync(candidate, ct).ConfigureAwait(false);
            if (existing is not null)
                return ToZoneAccount(existing, candidate, created: false);
        }

        // Tunnel CNAMEs need a zone. Add the registrable name when lookup misses;
        // a pending zone still accepts DNS writes before the registrar points at Cloudflare.
        var zoneName = ApexZoneName(hostname)
            ?? throw new InvalidOperationException(
                $"Hostname '{hostname}' is not a domain that can be added as a Cloudflare zone.");

        var account = await ResolveSingleAccountAsync(ct).ConfigureAwait(false);
        var created = await CreateZoneAsync(account.Id, zoneName, ct).ConfigureAwait(false);
        return ToZoneAccount(created, zoneName, created: true, fallbackAccountId: account.Id);
    }

    // Last dotted candidate. For api.example.com that is example.com, which lookup already tried.
    internal static string? ApexZoneName(string hostname)
    {
        string? apex = null;
        foreach (var candidate in EnumerateZoneCandidates(hostname))
        {
            if (candidate.Contains('.', StringComparison.Ordinal))
                apex = candidate;
        }

        return apex;
    }

    internal async Task<CloudflareTunnelCredentials> EnsureTunnelAsync(
        string accountId,
        string tunnelName,
        CancellationToken ct)
    {
        var existing = await FindTunnelByNameAsync(accountId, tunnelName, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var connectorToken = await GetConnectorTokenAsync(accountId, existing.Value.Id, ct).ConfigureAwait(false);
            return new CloudflareTunnelCredentials(existing.Value.Id, tunnelName, connectorToken);
        }

        using var content = JsonBody(
            new CloudflareCreateTunnelRequest { Name = tunnelName },
            CloudflareApiJsonContext.Default.CloudflareCreateTunnelRequest);
        using var response = await _http
            .PostAsync($"accounts/{accountId}/cfd_tunnel", content, ct)
            .ConfigureAwait(false);
        var created = await ReadRequiredResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareTunnelResult,
                ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(created.Id) || string.IsNullOrWhiteSpace(created.Token))
            throw new InvalidOperationException("Cloudflare tunnel create response missing id/token.");

        return new CloudflareTunnelCredentials(created.Id, tunnelName, created.Token);
    }

    internal async Task<string> GetConnectorTokenAsync(string accountId, string tunnelId, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"accounts/{accountId}/cfd_tunnel/{tunnelId}/token", ct)
            .ConfigureAwait(false);
        // Token endpoint returns { success, result: "<jwt>" } — result is a string.
        var token = await ReadRequiredResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseString,
                ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException($"Cloudflare tunnel token missing for {tunnelId}.");

        return token;
    }

    internal async Task WaitUntilHealthyAsync(
        string accountId,
        string tunnelId,
        TimeSpan timeout,
        PhaseLogger logger,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var status = await GetTunnelStatusAsync(accountId, tunnelId, ct).ConfigureAwait(false);
            if (status.IsHealthy)
            {
                logger.Info($"    cloudflare tunnel {tunnelId} is healthy ({status.ConnectionCount} connection(s))");
                return;
            }

            logger.Info($"    waiting for tunnel connector (status={status.Status}, connections={status.ConnectionCount})");
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Cloudflare tunnel {tunnelId} did not become healthy within {timeout.TotalMinutes:F0}m. " +
            "Check cloudflared logs, egress to Cloudflare on port 7844, and the connector token file.");
    }

    internal async Task PutIngressAsync(
        string accountId,
        string tunnelId,
        IReadOnlyList<string> hostnames,
        string originService,
        CancellationToken ct)
    {
        var ingress = new List<CloudflareIngressRule>(hostnames.Count + 1);
        foreach (var hostname in hostnames)
        {
            ingress.Add(new CloudflareIngressRule
            {
                Hostname = hostname,
                Service = originService,
                OriginRequest = new CloudflareOriginRequest(),
            });
        }

        ingress.Add(new CloudflareIngressRule { Service = "http_status:404" });

        using var content = JsonBody(
            new CloudflarePutIngressRequest { Config = new CloudflareTunnelConfig { Ingress = ingress } },
            CloudflareApiJsonContext.Default.CloudflarePutIngressRequest);
        using var response = await _http
            .PutAsync($"accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations", content, ct)
            .ConfigureAwait(false);
        _ = await ReadEnvelopeAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseJsonElement,
                ct)
            .ConfigureAwait(false);
    }

    internal async Task UpsertDnsCnameAsync(
        string zoneId,
        string hostname,
        string tunnelId,
        CancellationToken ct)
    {
        var request = new CloudflareDnsRecordRequest
        {
            Name = hostname,
            Content = $"{tunnelId}.cfargotunnel.com",
            Proxied = true,
            Ttl = 1,
        };
        using var content = JsonBody(request, CloudflareApiJsonContext.Default.CloudflareDnsRecordRequest);
        var existingId = await FindDnsRecordIdAsync(zoneId, hostname, ct).ConfigureAwait(false);

        if (existingId is null)
        {
            using var response = await _http
                .PostAsync($"zones/{zoneId}/dns_records", content, ct)
                .ConfigureAwait(false);
            _ = await ReadEnvelopeAsync(
                    response,
                    CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareDnsRecordResult,
                    ct)
                .ConfigureAwait(false);
            return;
        }

        using var patchResponse = await _http
            .PatchAsync($"zones/{zoneId}/dns_records/{existingId}", content, ct)
            .ConfigureAwait(false);
        _ = await ReadEnvelopeAsync(
                patchResponse,
                CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareDnsRecordResult,
                ct)
            .ConfigureAwait(false);
    }

    // orderAdvancedCertificate is the operator's explicit yes. False never places an order.
    internal async Task<EdgeCertificateResult> EnsureEdgeCertificatesAsync(
        CloudflareZoneAccount zone,
        IReadOnlyList<string> hostnames,
        bool orderAdvancedCertificate,
        CancellationToken ct)
    {
        var deep = SelectDeepHostnames(zone.ZoneName, hostnames);
        if (deep.Count == 0)
            return new EdgeCertificateResult(false, false, []);

        try
        {
            var enabledNow = await EnsureTotalTlsEnabledAsync(zone.ZoneId, ct).ConfigureAwait(false);
            var missing = await HostsMissingCertificateAsync(zone.ZoneId, deep, ct).ConfigureAwait(false);
            if (missing.Count == 0 || !orderAdvancedCertificate)
                return new EdgeCertificateResult(enabledNow, false, missing);

            var packHosts = new List<string>(missing.Count + 1) { NormalizeHost(zone.ZoneName) };
            packHosts.AddRange(missing);
            if (packHosts.Count > 50)
            {
                throw new InvalidOperationException(
                    $"Zone '{zone.ZoneName}' has {missing.Count} multi-level hostnames; an advanced certificate pack holds at most 50 names including the apex.");
            }

            await OrderAdvancedCertificateAsync(zone.ZoneId, packHosts, ct).ConfigureAwait(false);
            return new EdgeCertificateResult(enabledNow, true, missing);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("1450", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Multi-level hostnames need Advanced Certificate Manager on this Cloudflare zone. " +
                "Universal SSL only covers one subdomain level. " + ex.Message,
                ex);
        }
    }

    internal static IReadOnlyList<string> SelectDeepHostnames(string zoneName, IEnumerable<string> hostnames)
    {
        var zone = NormalizeHost(zoneName);
        return hostnames
            .Select(NormalizeHost)
            .Where(host => IsDeeperThanUniversalSsl(host, zone))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    internal static bool CertificateCoversHost(IEnumerable<string> packHosts, string hostname)
    {
        var host = NormalizeHost(hostname);
        foreach (var raw in packHosts)
        {
            var entry = NormalizeHost(raw);
            if (entry.Length == 0)
                continue;
            if (string.Equals(entry, host, StringComparison.Ordinal))
                return true;
            if (!entry.StartsWith("*.", StringComparison.Ordinal))
                continue;

            var suffix = entry[1..];
            if (!host.EndsWith(suffix, StringComparison.Ordinal) || host.Length <= suffix.Length)
                continue;
            var prefix = host[..^suffix.Length];
            if (!prefix.Contains('.', StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsDeeperThanUniversalSsl(string host, string zone)
    {
        if (host.Length == 0 || zone.Length == 0 || string.Equals(host, zone, StringComparison.Ordinal))
            return false;
        var parent = "." + zone;
        if (!host.EndsWith(parent, StringComparison.Ordinal))
            return false;
        var prefix = host[..^parent.Length];
        return prefix.Contains('.', StringComparison.Ordinal);
    }

    private static bool CertificateStatusCovers(string? status) => status is
        "active" or "initializing" or "pending_validation" or "pending_issuance"
        or "pending_deployment" or "staging_deployment" or "staging_active"
        or "backup_issued" or "holding_deployment";

    private static string NormalizeHost(string hostname) =>
        hostname.Trim().TrimEnd('.').ToLowerInvariant();

    private async Task<bool> EnsureTotalTlsEnabledAsync(string zoneId, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"zones/{zoneId}/acm/total_tls", ct)
            .ConfigureAwait(false);
        var settings = await ReadResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareTotalTlsSettings,
                ct)
            .ConfigureAwait(false);
        if (settings?.Enabled == true)
            return false;

        using var content = JsonBody(
            new CloudflareTotalTlsSettings { Enabled = true },
            CloudflareApiJsonContext.Default.CloudflareTotalTlsSettings);
        using var update = await _http
            .PostAsync($"zones/{zoneId}/acm/total_tls", content, ct)
            .ConfigureAwait(false);
        _ = await ReadEnvelopeAsync(
                update,
                CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareTotalTlsSettings,
                ct)
            .ConfigureAwait(false);
        return true;
    }

    private async Task<IReadOnlyList<string>> HostsMissingCertificateAsync(
        string zoneId,
        IReadOnlyList<string> hostnames,
        CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"zones/{zoneId}/ssl/certificate_packs?per_page=50", ct)
            .ConfigureAwait(false);
        var packs = await ReadResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseListCloudflareCertificatePackResult,
                ct)
            .ConfigureAwait(false) ?? [];

        var covering = packs.Where(pack => CertificateStatusCovers(pack.Status)).ToArray();
        return hostnames
            .Where(host => !covering.Any(pack => CertificateCoversHost(pack.Hosts ?? [], host)))
            .ToArray();
    }

    private async Task OrderAdvancedCertificateAsync(
        string zoneId,
        IReadOnlyList<string> hosts,
        CancellationToken ct)
    {
        using var content = JsonBody(
            new CloudflareCertificatePackOrderRequest { Hosts = [.. hosts] },
            CloudflareApiJsonContext.Default.CloudflareCertificatePackOrderRequest);
        using var response = await _http
            .PostAsync($"zones/{zoneId}/ssl/certificate_packs/order", content, ct)
            .ConfigureAwait(false);
        _ = await ReadRequiredResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareCertificatePackResult,
                ct)
            .ConfigureAwait(false);
    }

    internal static IEnumerable<string> EnumerateZoneCandidates(string hostname)
    {
        var host = hostname.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(host))
            yield break;

        yield return host;
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < parts.Length - 1; i++)
            yield return string.Join('.', parts.AsSpan(i));

        if (parts.Length >= 2)
            yield return string.Join('.', parts[^2], parts[^1]);
    }

    internal static string BuildPublicReadyUrl(string hostname)
        => $"https://{hostname.Trim().TrimEnd('.')}{HealthEndpoints.Ready}";

    internal static string GoogleAccessCallbackUri(string teamDomain)
        => $"{AccessTeamOrigin(teamDomain)}/cdn-cgi/access/callback";

    internal static string AccessTeamOrigin(string teamDomain)
        => $"https://{NormalizeTeamHost(teamDomain)}";

    internal static string NormalizeTeamHost(string teamDomain)
    {
        var raw = teamDomain.Trim();
        if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            raw = raw["https://".Length..];
        else if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            raw = raw["http://".Length..];
        return raw.TrimEnd('/');
    }

    internal async Task<CloudflareAccessOrganization> GetOrganizationAsync(string accountId, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"accounts/{accountId}/access/organizations", ct)
            .ConfigureAwait(false);
        var result = await ReadRequiredResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareAccessOrganizationResult,
                ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.AuthDomain))
        {
            throw new InvalidOperationException(
                "Cloudflare Access organization is missing auth_domain. Create a Zero Trust team in the dashboard first.");
        }

        return new CloudflareAccessOrganization(result.Name ?? "interfold", result.AuthDomain);
    }

    internal async Task<string> EnsureGoogleIdentityProviderAsync(
        string accountId,
        string clientId,
        string clientSecret,
        CancellationToken ct)
    {
        const string idpName = "interfold-google";
        var existing = await FindIdentityProviderByNameAsync(accountId, idpName, ct).ConfigureAwait(false);
        using var content = JsonBody(
            new CloudflareGoogleIdpRequest
            {
                Name = idpName,
                Config = new CloudflareGoogleIdpConfig { ClientId = clientId, ClientSecret = clientSecret },
            },
            CloudflareApiJsonContext.Default.CloudflareGoogleIdpRequest);

        if (existing is null)
        {
            using var response = await _http
                .PostAsync($"accounts/{accountId}/access/identity_providers", content, ct)
                .ConfigureAwait(false);
            var created = await ReadRequiredResultAsync(
                    response,
                    CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareIdentityProviderResult,
                    ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(created.Id))
                throw new InvalidOperationException("Cloudflare Access Google IdP create response missing id.");
            return created.Id;
        }

        using var put = await _http
            .PutAsync($"accounts/{accountId}/access/identity_providers/{existing}", content, ct)
            .ConfigureAwait(false);
        _ = await ReadEnvelopeAsync(
                put,
                CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareIdentityProviderResult,
                ct)
            .ConfigureAwait(false);
        return existing;
    }

    internal async Task<string> EnsureOidcIdentityProviderAsync(
        string accountId,
        string name,
        string clientId,
        string clientSecret,
        string workerOrigin,
        CancellationToken ct)
    {
        var origin = workerOrigin.TrimEnd('/');
        var existing = await FindIdentityProviderByNameAsync(accountId, name, ct).ConfigureAwait(false);
        using var content = JsonBody(
            new CloudflareOidcIdpRequest
            {
                Name = name,
                Config = new CloudflareOidcIdpConfig
                {
                    ClientId = clientId,
                    ClientSecret = clientSecret,
                    AuthUrl = $"{origin}/authorize/email",
                    TokenUrl = $"{origin}/token",
                    CertsUrl = $"{origin}/jwks.json",
                    PkceEnabled = false,
                    EmailClaimName = "email",
                    Claims = ["id"],
                    Scopes = ["openid", "email", "profile"],
                },
            },
            CloudflareApiJsonContext.Default.CloudflareOidcIdpRequest);

        if (existing is null)
        {
            using var response = await _http
                .PostAsync($"accounts/{accountId}/access/identity_providers", content, ct)
                .ConfigureAwait(false);
            var created = await ReadRequiredResultAsync(
                    response,
                    CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareIdentityProviderResult,
                    ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(created.Id))
                throw new InvalidOperationException("Cloudflare Access OIDC IdP create response missing id.");
            return created.Id;
        }

        using var put = await _http
            .PutAsync($"accounts/{accountId}/access/identity_providers/{existing}", content, ct)
            .ConfigureAwait(false);
        _ = await ReadEnvelopeAsync(
                put,
                CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareIdentityProviderResult,
                ct)
            .ConfigureAwait(false);
        return existing;
    }

    internal async Task<string> EnsureKvNamespaceAsync(
        string accountId,
        string title,
        CancellationToken ct)
    {
        var existing = await FindKvNamespaceByTitleAsync(accountId, title, ct).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        using var content = JsonBody(
            new CloudflareKvNamespaceRequest { Title = title },
            CloudflareApiJsonContext.Default.CloudflareKvNamespaceRequest);
        using var response = await _http
            .PostAsync($"accounts/{accountId}/storage/kv/namespaces", content, ct)
            .ConfigureAwait(false);
        var created = await ReadRequiredResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareKvNamespaceResult,
                ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(created.Id))
            throw new InvalidOperationException($"Cloudflare KV namespace '{title}' create response missing id.");
        return created.Id;
    }

    internal async Task<string> EnsureWorkersSubdomainAsync(string accountId, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"accounts/{accountId}/workers/subdomain", ct)
            .ConfigureAwait(false);
        var result = await ReadRequiredResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareWorkersSubdomainResult,
                ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.Subdomain))
        {
            throw new InvalidOperationException(
                "Cloudflare account has no workers.dev subdomain. Enable it under Workers → Settings, then re-run Access.");
        }

        return result.Subdomain.Trim();
    }

    internal async Task<string> EnsureDiscordOidcWorkerAsync(
        string accountId,
        string scriptName,
        string bundlePath,
        string configPath,
        string kvNamespaceId,
        string workersSubdomain,
        CancellationToken ct)
    {
        var metadata = new CloudflareWorkerUploadMetadata
        {
            MainModule = "worker.js",
            CompatibilityDate = "2022-12-24",
            Bindings =
            [
                new CloudflareWorkerBinding
                {
                    Type = "kv_namespace",
                    Name = "KV",
                    NamespaceId = kvNamespaceId,
                },
            ],
        };

        using var form = new MultipartFormDataContent();
        var metadataJson = JsonSerializer.Serialize(
            metadata,
            CloudflareApiJsonContext.Default.CloudflareWorkerUploadMetadata);
        form.Add(new StringContent(metadataJson, Encoding.UTF8, "application/json"), "metadata");

        var workerBytes = await File.ReadAllBytesAsync(bundlePath, ct).ConfigureAwait(false);
        var workerContent = new ByteArrayContent(workerBytes);
        workerContent.Headers.ContentType = new MediaTypeHeaderValue("application/javascript+module");
        form.Add(workerContent, "worker.js", "worker.js");

        var configJson = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false);
        var configBytes = Encoding.UTF8.GetBytes(DiscordOidcWorkerSource.ToConfigEsModule(configJson));
        var configContent = new ByteArrayContent(configBytes);
        configContent.Headers.ContentType = new MediaTypeHeaderValue("application/javascript+module");
        form.Add(configContent, "config.json", "config.json");

        using var upload = await _http
            .PutAsync($"accounts/{accountId}/workers/scripts/{scriptName}", form, ct)
            .ConfigureAwait(false);
        _ = await ReadEnvelopeAsync(
                upload,
                CloudflareApiJsonContext.Default.CloudflareApiResponseJsonElement,
                ct)
            .ConfigureAwait(false);

        using var enableContent = JsonBody(
            new CloudflareWorkersScriptSubdomainRequest { Enabled = true },
            CloudflareApiJsonContext.Default.CloudflareWorkersScriptSubdomainRequest);
        using var enable = await _http
            .PostAsync($"accounts/{accountId}/workers/scripts/{scriptName}/subdomain", enableContent, ct)
            .ConfigureAwait(false);
        _ = await ReadEnvelopeAsync(
                enable,
                CloudflareApiJsonContext.Default.CloudflareApiResponseJsonElement,
                ct)
            .ConfigureAwait(false);

        return $"https://{scriptName}.{workersSubdomain}.workers.dev";
    }

    internal async Task<CloudflareAccessApp> EnsureSelfHostedAppAsync(
        string accountId,
        string hostname,
        string identityProviderId,
        CancellationToken ct,
        bool optionsPreflightBypass = false)
        => await EnsureSelfHostedAppAsync(
                accountId,
                hostname,
                [identityProviderId],
                autoRedirectToIdentity: true,
                ct,
                optionsPreflightBypass)
            .ConfigureAwait(false);

    internal async Task<CloudflareAccessApp> EnsureSelfHostedAppAsync(
        string accountId,
        string hostname,
        IReadOnlyList<string> allowedIdps,
        bool autoRedirectToIdentity,
        CancellationToken ct,
        bool optionsPreflightBypass = false)
    {
        var existing = await FindAppByDomainAsync(accountId, hostname, ct).ConfigureAwait(false);
        using var content = JsonBody(
            new CloudflareSelfHostedAppRequest
            {
                Name = $"interfold-{hostname.Replace('/', '-')}",
                Domain = hostname,
                AutoRedirectToIdentity = autoRedirectToIdentity,
                AllowedIdps = [.. allowedIdps],
                OptionsPreflightBypass = optionsPreflightBypass,
            },
            CloudflareApiJsonContext.Default.CloudflareSelfHostedAppRequest);

        if (existing is null)
        {
            using var response = await _http
                .PostAsync($"accounts/{accountId}/access/apps", content, ct)
                .ConfigureAwait(false);
            var created = await ReadRequiredResultAsync(
                    response,
                    CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareAccessAppResult,
                    ct)
                .ConfigureAwait(false);
            return ToAccessApp(created, hostname);
        }

        using var put = await _http
            .PutAsync($"accounts/{accountId}/access/apps/{existing.Id}", content, ct)
            .ConfigureAwait(false);
        var updated = ToAccessApp(
            await ReadRequiredResultAsync(
                    put,
                    CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareAccessAppResult,
                    ct)
                .ConfigureAwait(false),
            hostname);
        return updated with { Id = existing.Id, Aud = string.IsNullOrWhiteSpace(updated.Aud) ? existing.Aud : updated.Aud };
    }

    internal async Task ReplaceAppPoliciesAsync(
        string accountId,
        string appId,
        IReadOnlyList<CloudflareAccessPolicySpec> policies,
        CancellationToken ct)
    {
        var existingIds = await ListPolicyIdsAsync(accountId, appId, ct).ConfigureAwait(false);
        foreach (var policyId in existingIds)
        {
            using var del = await _http
                .DeleteAsync($"accounts/{accountId}/access/apps/{appId}/policies/{policyId}", ct)
                .ConfigureAwait(false);
            _ = await ReadEnvelopeAsync(
                    del,
                    CloudflareApiJsonContext.Default.CloudflareApiResponseJsonElement,
                    ct)
                .ConfigureAwait(false);
        }

        foreach (var policy in policies)
        {
            using var content = JsonBody(ToPolicyRequest(policy), CloudflareApiJsonContext.Default.CloudflareAccessPolicyRequest);
            using var response = await _http
                .PostAsync($"accounts/{accountId}/access/apps/{appId}/policies", content, ct)
                .ConfigureAwait(false);
            _ = await ReadEnvelopeAsync(
                    response,
                    CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareAccessPolicyResult,
                    ct)
                .ConfigureAwait(false);
        }
    }

    internal async Task<CloudflareAccessServiceToken> EnsureServiceTokenAsync(
        string accountId,
        string name,
        string? existingClientId,
        string? existingClientSecret,
        CancellationToken ct)
    {
        var existing = await FindServiceTokenByNameAsync(accountId, name, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(existingClientId)
                && !string.IsNullOrWhiteSpace(existingClientSecret)
                && string.Equals(existing.Value.ClientId, existingClientId, StringComparison.Ordinal))
            {
                return new CloudflareAccessServiceToken(existing.Value.Id, existing.Value.ClientId, existingClientSecret);
            }

            throw new InvalidOperationException(
                $"Cloudflare Access service token '{name}' already exists but the local secret file is missing or does not match. " +
                "Delete the token in Zero Trust and re-run bootstrap, or restore secrets/cloudflare-access-service.token.");
        }

        using var content = JsonBody(
            new CloudflareServiceTokenRequest { Name = name },
            CloudflareApiJsonContext.Default.CloudflareServiceTokenRequest);
        using var response = await _http
            .PostAsync($"accounts/{accountId}/access/service_tokens", content, ct)
            .ConfigureAwait(false);
        var result = await ReadRequiredResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareServiceTokenResult,
                ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.Id)
            || string.IsNullOrWhiteSpace(result.ClientId)
            || string.IsNullOrWhiteSpace(result.ClientSecret))
        {
            throw new InvalidOperationException(
                "Cloudflare Access service-token create response missing id/client_id/client_secret.");
        }

        return new CloudflareAccessServiceToken(result.Id, result.ClientId, result.ClientSecret);
    }

    private async Task<string?> FindIdentityProviderByNameAsync(string accountId, string name, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"accounts/{accountId}/access/identity_providers", ct)
            .ConfigureAwait(false);
        var idps = await ReadResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseListCloudflareIdentityProviderResult,
                ct)
            .ConfigureAwait(false);
        if (idps is null)
            return null;

        foreach (var idp in idps)
        {
            if (!string.IsNullOrWhiteSpace(idp.Id)
                && string.Equals(idp.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return idp.Id;
            }
        }

        return null;
    }

    private async Task<string?> FindKvNamespaceByTitleAsync(string accountId, string title, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"accounts/{accountId}/storage/kv/namespaces?per_page=100", ct)
            .ConfigureAwait(false);
        var namespaces = await ReadResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseListCloudflareKvNamespaceResult,
                ct)
            .ConfigureAwait(false);
        if (namespaces is null)
            return null;

        foreach (var ns in namespaces)
        {
            if (!string.IsNullOrWhiteSpace(ns.Id)
                && string.Equals(ns.Title, title, StringComparison.OrdinalIgnoreCase))
            {
                return ns.Id;
            }
        }

        return null;
    }

    private async Task<CloudflareAccessApp?> FindAppByDomainAsync(string accountId, string domain, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"accounts/{accountId}/access/apps", ct)
            .ConfigureAwait(false);
        var apps = await ReadResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseListCloudflareAccessAppResult,
                ct)
            .ConfigureAwait(false);
        if (apps is null)
            return null;

        foreach (var app in apps)
        {
            if (!string.Equals(app.Domain, domain, StringComparison.OrdinalIgnoreCase))
                continue;
            return ToAccessApp(app, domain);
        }

        return null;
    }

    private async Task<List<string>> ListPolicyIdsAsync(string accountId, string appId, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"accounts/{accountId}/access/apps/{appId}/policies", ct)
            .ConfigureAwait(false);
        var policies = await ReadResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseListCloudflareAccessPolicyResult,
                ct)
            .ConfigureAwait(false);
        if (policies is null)
            return [];

        return [.. policies
            .Select(p => p.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)];
    }

    private async Task<(string Id, string ClientId)?> FindServiceTokenByNameAsync(
        string accountId,
        string name,
        CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"accounts/{accountId}/access/service_tokens", ct)
            .ConfigureAwait(false);
        var tokens = await ReadResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseListCloudflareServiceTokenResult,
                ct)
            .ConfigureAwait(false);
        if (tokens is null)
            return null;

        foreach (var token in tokens)
        {
            if (!string.IsNullOrWhiteSpace(token.Id)
                && !string.IsNullOrWhiteSpace(token.ClientId)
                && string.Equals(token.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return (token.Id, token.ClientId);
            }
        }

        return null;
    }

    private static CloudflareAccessApp ToAccessApp(CloudflareAccessAppResult result, string fallbackDomain)
    {
        if (string.IsNullOrWhiteSpace(result.Id))
            throw new InvalidOperationException("Cloudflare Access app response missing id.");
        return new CloudflareAccessApp(result.Id, result.Domain ?? fallbackDomain, result.Aud ?? string.Empty);
    }

    private static CloudflareAccessPolicyRequest ToPolicyRequest(CloudflareAccessPolicySpec policy)
        => new()
        {
            Name = policy.Name,
            Decision = policy.Decision,
            Include = [.. policy.Include.Select(CloudflareAccessPolicyCondition.From)],
            Require = [.. policy.Require.Select(CloudflareAccessPolicyCondition.From)],
        };

    private async Task<CloudflareZoneResult?> FindZoneByNameAsync(string name, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"zones?name={Uri.EscapeDataString(name)}", ct)
            .ConfigureAwait(false);
        var zones = await ReadResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseListCloudflareZoneResult,
                ct)
            .ConfigureAwait(false);
        if (zones is null || zones.Count == 0)
            return null;

        return zones[0];
    }

    private async Task<(string Id, string Name)> ResolveSingleAccountAsync(CancellationToken ct)
    {
        using var response = await _http
            .GetAsync("accounts?per_page=50", ct)
            .ConfigureAwait(false);
        var accounts = await ReadResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseListCloudflareAccountResult,
                ct)
            .ConfigureAwait(false);
        var usable = accounts?
            .Where(account => !string.IsNullOrWhiteSpace(account.Id))
            .ToList() ?? [];
        if (usable.Count == 0)
        {
            throw new InvalidOperationException(
                "Cloudflare token returned no accounts. Zone create needs a token scoped to one account.");
        }

        if (usable.Count > 1)
        {
            var names = string.Join(", ", usable.Select(account => account.Name ?? account.Id));
            throw new InvalidOperationException(
                $"Cloudflare token can see multiple accounts ({names}). " +
                "Scope it to one account so a missing zone is created there.");
        }

        return (usable[0].Id!, usable[0].Name ?? usable[0].Id!);
    }

    private async Task<CloudflareZoneResult> CreateZoneAsync(string accountId, string zoneName, CancellationToken ct)
    {
        using var content = JsonBody(
            new CloudflareCreateZoneRequest
            {
                Name = zoneName,
                Account = new CloudflareAccountRef { Id = accountId },
            },
            CloudflareApiJsonContext.Default.CloudflareCreateZoneRequest);
        using var response = await _http
            .PostAsync("zones", content, ct)
            .ConfigureAwait(false);
        return await ReadRequiredResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareZoneResult,
                ct)
            .ConfigureAwait(false);
    }

    private static CloudflareZoneAccount ToZoneAccount(
        CloudflareZoneResult zone,
        string fallbackName,
        bool created,
        string? fallbackAccountId = null)
    {
        var zoneId = zone.Id;
        var accountId = zone.Account?.Id ?? fallbackAccountId;
        if (string.IsNullOrWhiteSpace(zoneId) || string.IsNullOrWhiteSpace(accountId))
        {
            throw new InvalidOperationException(
                $"Cloudflare zone '{fallbackName}' response missing id/account.id.");
        }

        IReadOnlyList<string>? nameServers = null;
        if (created)
        {
            nameServers = (zone.NameServers ?? [])
                .Where(ns => !string.IsNullOrWhiteSpace(ns))
                .ToArray();
        }

        return new CloudflareZoneAccount(zoneId, accountId, zone.Name ?? fallbackName, created, nameServers);
    }

    private async Task<(string Id, string Name)?> FindTunnelByNameAsync(
        string accountId,
        string name,
        CancellationToken ct)
    {
        using var response = await _http
            .GetAsync(
                $"accounts/{accountId}/cfd_tunnel?name={Uri.EscapeDataString(name)}&is_deleted=false",
                ct)
            .ConfigureAwait(false);
        var tunnels = await ReadResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseListCloudflareTunnelResult,
                ct)
            .ConfigureAwait(false);
        if (tunnels is null)
            return null;

        foreach (var tunnel in tunnels)
        {
            if (string.IsNullOrWhiteSpace(tunnel.Id))
                continue;
            if (string.Equals(tunnel.Name, name, StringComparison.OrdinalIgnoreCase))
                return (tunnel.Id, tunnel.Name ?? name);
        }

        return null;
    }

    private async Task<TunnelStatus> GetTunnelStatusAsync(string accountId, string tunnelId, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"accounts/{accountId}/cfd_tunnel/{tunnelId}", ct)
            .ConfigureAwait(false);
        var result = await ReadRequiredResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseCloudflareTunnelResult,
                ct)
            .ConfigureAwait(false);
        var status = string.IsNullOrWhiteSpace(result.Status) ? "unknown" : result.Status;
        var connections = result.Connections.Count;
        var healthy = connections > 0
            || string.Equals(status, "healthy", StringComparison.OrdinalIgnoreCase);
        return new TunnelStatus(status, connections, healthy);
    }

    private async Task<string?> FindDnsRecordIdAsync(string zoneId, string hostname, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync(
                $"zones/{zoneId}/dns_records?type=CNAME&name={Uri.EscapeDataString(hostname)}",
                ct)
            .ConfigureAwait(false);
        var records = await ReadResultAsync(
                response,
                CloudflareApiJsonContext.Default.CloudflareApiResponseListCloudflareDnsRecordResult,
                ct)
            .ConfigureAwait(false);
        if (records is null || records.Count == 0)
            return null;

        return records[0].Id;
    }

    private static StringContent JsonBody<T>(T value, JsonTypeInfo<T> typeInfo)
        => new(JsonSerializer.Serialize(value, typeInfo), Encoding.UTF8, "application/json");

    private static async Task<TResult> ReadRequiredResultAsync<TResult>(
        HttpResponseMessage response,
        JsonTypeInfo<CloudflareApiResponse<TResult>> typeInfo,
        CancellationToken ct)
    {
        var envelope = await ReadEnvelopeAsync(response, typeInfo, ct).ConfigureAwait(false);
        if (envelope.Result is null)
            throw new InvalidOperationException("Cloudflare API response missing result.");
        return envelope.Result;
    }

    private static async Task<TResult?> ReadResultAsync<TResult>(
        HttpResponseMessage response,
        JsonTypeInfo<CloudflareApiResponse<TResult>> typeInfo,
        CancellationToken ct)
    {
        var envelope = await ReadEnvelopeAsync(response, typeInfo, ct).ConfigureAwait(false);
        return envelope.Result;
    }

    private static async Task<CloudflareApiResponse<TResult>> ReadEnvelopeAsync<TResult>(
        HttpResponseMessage response,
        JsonTypeInfo<CloudflareApiResponse<TResult>> typeInfo,
        CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        CloudflareApiResponse<TResult>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Cloudflare API returned non-JSON (HTTP {(int)response.StatusCode}): {Truncate(json)}", ex);
        }

        if (envelope is null)
        {
            throw new InvalidOperationException(
                $"Cloudflare API returned empty JSON (HTTP {(int)response.StatusCode}).");
        }

        if (!response.IsSuccessStatusCode || !envelope.Success)
        {
            var errors = FormatErrors(envelope.Errors);
            throw new InvalidOperationException(
                $"Cloudflare API error: {(string.IsNullOrEmpty(errors) ? $"HTTP {(int)response.StatusCode}" : errors)}");
        }

        return envelope;
    }

    private static string FormatErrors(List<CloudflareApiError> errors)
    {
        if (errors.Count == 0)
            return string.Empty;

        return string.Join("; ", errors.Select(err =>
        {
            var code = err.Code?.ToString() ?? "?";
            return $"{code}: {err.Message}";
        }));
    }

    private static string Truncate(string s)
        => s.Length <= 200 ? s : s[..200] + "…";

    private readonly record struct TunnelStatus(string Status, int ConnectionCount, bool IsHealthy);
}

internal readonly record struct EdgeCertificateResult(
    bool EnabledTotalTls,
    bool OrderedAdvancedCertificate,
    IReadOnlyList<string> AdvancedHosts);

internal sealed record CloudflareZoneAccount(
    string ZoneId,
    string AccountId,
    string ZoneName,
    bool Created = false,
    IReadOnlyList<string>? NameServers = null);

internal sealed record CloudflareTunnelCredentials(string TunnelId, string Name, string ConnectorToken);

public sealed class CloudflareTunnelState
{
    [JsonPropertyName("tunnelId")]
    public string TunnelId { get; set; } = string.Empty;

    [JsonPropertyName("accountId")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("zoneId")]
    public string ZoneId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

internal sealed record CloudflareAccessOrganization(string Name, string AuthDomain);

internal sealed record CloudflareAccessApp(string Id, string Domain, string Aud);

internal sealed record CloudflareAccessServiceToken(string Id, string ClientId, string ClientSecret);

internal sealed record CloudflareAccessPolicySpec(
    string Name,
    string Decision,
    IReadOnlyList<CloudflareAccessPolicyRule> Include,
    IReadOnlyList<CloudflareAccessPolicyRule> Require);

internal enum CloudflareAccessPolicyRuleKind
{
    Email,
    EmailDomain,
    LoginMethod,
    Everyone,
    ServiceToken,
}

internal sealed record CloudflareAccessPolicyRule(CloudflareAccessPolicyRuleKind Kind, string Value = "");

public sealed class CloudflareAccessState
{
    [JsonPropertyName("teamDomain")]
    public string TeamDomain { get; set; } = string.Empty;

    [JsonPropertyName("aud")]
    public string Aud { get; set; } = string.Empty;

    [JsonPropertyName("identityProviderId")]
    public string IdentityProviderId { get; set; } = string.Empty;

    [JsonPropertyName("discordIdentityProviderId")]
    public string DiscordIdentityProviderId { get; set; } = string.Empty;

    [JsonPropertyName("discordWorkerName")]
    public string DiscordWorkerName { get; set; } = string.Empty;

    [JsonPropertyName("discordKvNamespaceId")]
    public string DiscordKvNamespaceId { get; set; } = string.Empty;

    [JsonPropertyName("discordWorkerUrl")]
    public string DiscordWorkerUrl { get; set; } = string.Empty;

    [JsonPropertyName("appIds")]
    public Dictionary<string, string> AppIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
