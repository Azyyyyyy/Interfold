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
        http.DefaultRequestHeaders.UserAgent.ParseAdd("interfold-bootstrap/1.0");
        return new CloudflareTunnelClient(http);
    }

    internal async Task<CloudflareZoneAccount> ResolveZoneAndAccountAsync(string hostname, CancellationToken ct)
    {
        foreach (var candidate in EnumerateZoneCandidates(hostname))
        {
            using var response = await _http
                .GetAsync($"zones?name={Uri.EscapeDataString(candidate)}", ct)
                .ConfigureAwait(false);
            var zones = await ReadResultAsync(
                    response,
                    CloudflareApiJsonContext.Default.CloudflareApiResponseListCloudflareZoneResult,
                    ct)
                .ConfigureAwait(false);
            if (zones is null || zones.Count == 0)
                continue;

            var zone = zones[0];
            var zoneId = zone.Id;
            var accountId = zone.Account?.Id;
            if (string.IsNullOrWhiteSpace(zoneId) || string.IsNullOrWhiteSpace(accountId))
            {
                throw new InvalidOperationException(
                    $"Cloudflare zone '{candidate}' response missing id/account.id.");
            }

            return new CloudflareZoneAccount(zoneId, accountId, zone.Name ?? candidate);
        }

        throw new InvalidOperationException(
            $"No Cloudflare zone found for hostname '{hostname}'. " +
            "Confirm the domain is on Cloudflare and the API token has Zone:DNS:Edit.");
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
    {
        var host = NormalizeTeamHost(teamDomain);
        return $"https://{host}/cdn-cgi/access/callback";
    }

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
        using var doc = await ReadDocumentAsync(response, ct).ConfigureAwait(false);
        var result = RequireResultObject(doc);
        var authDomain = result.TryGetProperty("auth_domain", out var ad) ? ad.GetString() : null;
        var name = result.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(authDomain))
        {
            throw new InvalidOperationException(
                "Cloudflare Access organization is missing auth_domain. Create a Zero Trust team in the dashboard first.");
        }

        return new CloudflareAccessOrganization(name ?? "interfold", authDomain);
    }

    internal async Task<string> EnsureGoogleIdentityProviderAsync(
        string accountId,
        string clientId,
        string clientSecret,
        CancellationToken ct)
    {
        const string idpName = "interfold-google";
        var existing = await FindIdentityProviderByNameAsync(accountId, idpName, ct).ConfigureAwait(false);
        using var content = JsonBody(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("name", idpName);
            writer.WriteString("type", "google");
            writer.WritePropertyName("config");
            writer.WriteStartObject();
            writer.WriteString("client_id", clientId);
            writer.WriteString("client_secret", clientSecret);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

        if (existing is null)
        {
            using var response = await _http
                .PostAsync($"accounts/{accountId}/access/identity_providers", content, ct)
                .ConfigureAwait(false);
            using var doc = await ReadDocumentAsync(response, ct).ConfigureAwait(false);
            var id = RequireResultObject(doc).GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("Cloudflare Access Google IdP create response missing id.");
            return id;
        }

        using var put = await _http
            .PutAsync($"accounts/{accountId}/access/identity_providers/{existing}", content, ct)
            .ConfigureAwait(false);
        _ = await ReadDocumentAsync(put, ct).ConfigureAwait(false);
        return existing;
    }

    internal async Task<CloudflareAccessApp> EnsureSelfHostedAppAsync(
        string accountId,
        string hostname,
        string identityProviderId,
        CancellationToken ct)
    {
        var existing = await FindAppByDomainAsync(accountId, hostname, ct).ConfigureAwait(false);
        using var content = JsonBody(writer => WriteSelfHostedApp(writer, hostname, identityProviderId));

        if (existing is null)
        {
            using var response = await _http
                .PostAsync($"accounts/{accountId}/access/apps", content, ct)
                .ConfigureAwait(false);
            using var doc = await ReadDocumentAsync(response, ct).ConfigureAwait(false);
            return ReadAccessApp(RequireResultObject(doc), hostname);
        }

        using var put = await _http
            .PutAsync($"accounts/{accountId}/access/apps/{existing.Id}", content, ct)
            .ConfigureAwait(false);
        using var putDoc = await ReadDocumentAsync(put, ct).ConfigureAwait(false);
        var updated = ReadAccessApp(RequireResultObject(putDoc), hostname);
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
            _ = await ReadDocumentAsync(del, ct).ConfigureAwait(false);
        }

        foreach (var policy in policies)
        {
            using var content = JsonBody(writer => WritePolicy(writer, policy));
            using var response = await _http
                .PostAsync($"accounts/{accountId}/access/apps/{appId}/policies", content, ct)
                .ConfigureAwait(false);
            _ = await ReadDocumentAsync(response, ct).ConfigureAwait(false);
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

        using var content = JsonBody(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteEndObject();
        });
        using var response = await _http
            .PostAsync($"accounts/{accountId}/access/service_tokens", content, ct)
            .ConfigureAwait(false);
        using var doc = await ReadDocumentAsync(response, ct).ConfigureAwait(false);
        var result = RequireResultObject(doc);
        var id = result.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var clientId = result.TryGetProperty("client_id", out var cid) ? cid.GetString() : null;
        var clientSecret = result.TryGetProperty("client_secret", out var csec) ? csec.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("Cloudflare Access service-token create response missing id/client_id/client_secret.");

        return new CloudflareAccessServiceToken(id, clientId, clientSecret);
    }

    private async Task<string?> FindIdentityProviderByNameAsync(string accountId, string name, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"accounts/{accountId}/access/identity_providers", ct)
            .ConfigureAwait(false);
        using var doc = await ReadDocumentAsync(response, ct).ConfigureAwait(false);
        if (!TryGetResultArray(doc, out var idps))
            return null;

        foreach (var idp in idps.EnumerateArray())
        {
            var idpName = idp.TryGetProperty("name", out var n) ? n.GetString() : null;
            var id = idp.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id) && string.Equals(idpName, name, StringComparison.OrdinalIgnoreCase))
                return id;
        }

        return null;
    }

    private async Task<CloudflareAccessApp?> FindAppByDomainAsync(string accountId, string domain, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"accounts/{accountId}/access/apps", ct)
            .ConfigureAwait(false);
        using var doc = await ReadDocumentAsync(response, ct).ConfigureAwait(false);
        if (!TryGetResultArray(doc, out var apps))
            return null;

        foreach (var app in apps.EnumerateArray())
        {
            var appDomain = app.TryGetProperty("domain", out var d) ? d.GetString() : null;
            if (!string.Equals(appDomain, domain, StringComparison.OrdinalIgnoreCase))
                continue;
            return ReadAccessApp(app, domain);
        }

        return null;
    }

    private async Task<List<string>> ListPolicyIdsAsync(string accountId, string appId, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"accounts/{accountId}/access/apps/{appId}/policies", ct)
            .ConfigureAwait(false);
        using var doc = await ReadDocumentAsync(response, ct).ConfigureAwait(false);
        var ids = new List<string>();
        if (!TryGetResultArray(doc, out var policies))
            return ids;

        foreach (var policy in policies.EnumerateArray())
        {
            var id = policy.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id))
                ids.Add(id);
        }

        return ids;
    }

    private async Task<(string Id, string ClientId)?> FindServiceTokenByNameAsync(
        string accountId,
        string name,
        CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"accounts/{accountId}/access/service_tokens", ct)
            .ConfigureAwait(false);
        using var doc = await ReadDocumentAsync(response, ct).ConfigureAwait(false);
        if (!TryGetResultArray(doc, out var tokens))
            return null;

        foreach (var token in tokens.EnumerateArray())
        {
            var tokenName = token.TryGetProperty("name", out var n) ? n.GetString() : null;
            var id = token.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var clientId = token.TryGetProperty("client_id", out var cid) ? cid.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id)
                && !string.IsNullOrWhiteSpace(clientId)
                && string.Equals(tokenName, name, StringComparison.OrdinalIgnoreCase))
            {
                return (id, clientId);
            }
        }

        return null;
    }

    private static void WriteSelfHostedApp(Utf8JsonWriter writer, string hostname, string identityProviderId)
    {
        writer.WriteStartObject();
        writer.WriteString("name", $"interfold-{hostname.Replace('/', '-')}");
        writer.WriteString("domain", hostname);
        writer.WriteString("type", "self_hosted");
        writer.WriteString("session_duration", "24h");
        writer.WriteBoolean("auto_redirect_to_identity", true);
        writer.WritePropertyName("allowed_idps");
        writer.WriteStartArray();
        writer.WriteStringValue(identityProviderId);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WritePolicy(Utf8JsonWriter writer, CloudflareAccessPolicySpec policy)
    {
        writer.WriteStartObject();
        writer.WriteString("name", policy.Name);
        writer.WriteString("decision", policy.Decision);
        writer.WritePropertyName("include");
        writer.WriteStartArray();
        foreach (var rule in policy.Include)
            WritePolicyRule(writer, rule);
        writer.WriteEndArray();
        writer.WritePropertyName("require");
        writer.WriteStartArray();
        foreach (var rule in policy.Require)
            WritePolicyRule(writer, rule);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WritePolicyRule(Utf8JsonWriter writer, CloudflareAccessPolicyRule rule)
    {
        writer.WriteStartObject();
        switch (rule.Kind)
        {
            case CloudflareAccessPolicyRuleKind.Email:
                writer.WritePropertyName("email");
                writer.WriteStartObject();
                writer.WriteString("email", rule.Value);
                writer.WriteEndObject();
                break;
            case CloudflareAccessPolicyRuleKind.EmailDomain:
                writer.WritePropertyName("email_domain");
                writer.WriteStartObject();
                writer.WriteString("domain", rule.Value);
                writer.WriteEndObject();
                break;
            case CloudflareAccessPolicyRuleKind.LoginMethod:
                writer.WritePropertyName("login_method");
                writer.WriteStartObject();
                writer.WriteString("id", rule.Value);
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
                writer.WriteString("token_id", rule.Value);
                writer.WriteEndObject();
                break;
        }

        writer.WriteEndObject();
    }

    private static CloudflareAccessApp ReadAccessApp(JsonElement result, string fallbackDomain)
    {
        var id = result.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var aud = result.TryGetProperty("aud", out var audEl) ? audEl.GetString() : null;
        var domain = result.TryGetProperty("domain", out var d) ? d.GetString() : fallbackDomain;
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Cloudflare Access app response missing id.");
        return new CloudflareAccessApp(id, domain ?? fallbackDomain, aud ?? string.Empty);
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
            var code = err.Code is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } el
                ? el.ToString()
                : "?";
            return $"{code}: {err.Message}";
        }));
    }

    private static string Truncate(string s)
        => s.Length <= 200 ? s : s[..200] + "…";

    private readonly record struct TunnelStatus(string Status, int ConnectionCount, bool IsHealthy);
}

internal sealed record CloudflareZoneAccount(string ZoneId, string AccountId, string ZoneName);

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

    [JsonPropertyName("appIds")]
    public Dictionary<string, string> AppIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
