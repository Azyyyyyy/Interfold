using System.Net;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.Util;

/// <summary>
/// Shared HTTP readiness probe for the API's <c>/health/ready</c> endpoint. Used by
/// <see cref="Phases.LaunchPhase"/> (post-launch health gate) and
/// <see cref="Phases.UpdateImagesPhase"/> (post-recreate health check).
/// </summary>
internal static class ApiReadinessProbe
{
    private const int DefaultHttpPort = 80;
    private const int DefaultHttpsPort = 443;

    /// <summary>URL for polling readiness. Tunnel publishes no host ports, so that case
    /// uses the public hostname instead of localhost.</summary>
    internal static string ResolveReadyUrl(BootstrapConfig config)
    {
        if (config.Edge.Cloudflare.Enabled)
        {
            var hosts = CloudflareTunnelPhase.ResolvePublicHostnames(config);
            if (hosts.Count == 0)
            {
                throw new InvalidOperationException(
                    "Cloudflare Tunnel is enabled but no public DNS hostname is configured for the readiness probe.");
            }

            return CloudflareTunnelClient.BuildPublicReadyUrl(hosts[0]);
        }

        if (config.Edge.TlsMode == EdgeTlsMode.None)
        {
            var suffix = config.Edge.Ports.Http == DefaultHttpPort ? string.Empty : $":{config.Edge.Ports.Http}";
            return $"http://localhost{suffix}{HealthEndpoints.Ready}";
        }

        var httpsSuffix = config.Edge.Ports.Https == DefaultHttpsPort ? string.Empty : $":{config.Edge.Ports.Https}";
        return $"https://localhost{httpsSuffix}{HealthEndpoints.Ready}";
    }

    /// <summary>
    /// Polls the edge-proxied ready endpoint until it returns 200 or
    /// <paramref name="deadline"/> is reached. Returns <c>null</c> on success, or a short
    /// operator-facing description on timeout.
    /// </summary>
    public static async Task<string?> TryWaitUntilAsync(
        BootstrapConfig config,
        DateTime deadline,
        PhaseLogger logger,
        CancellationToken ct,
        string? outputDir = null)
    {
        // An allowlisted Access app rejects this machine. The service token is the
        // non-identity policy; a 302 to the login page is not readiness.
        CloudflareAccessServiceToken? serviceToken = null;
        if (config.Edge.Cloudflare.Access.Enabled)
        {
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                return "Cloudflare Access is enabled but no output directory was given for the service token.";
            }

            serviceToken = CloudflareAccessPhase.TryLoadServiceToken(outputDir);
            if (serviceToken is null)
            {
                return "Cloudflare Access allowlist is on, but "
                    + CloudflareAccessPhase.ServiceTokenPath(outputDir)
                    + " is missing. update-images cannot reach /health/ready.";
            }
        }

        using var http = CreateClient(config);
        var url = ResolveReadyUrl(config);
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (serviceToken is not null)
                {
                    request.Headers.TryAddWithoutValidation(InterfoldHeaders.CfAccessClientId, serviceToken.ClientId);
                    request.Headers.TryAddWithoutValidation(InterfoldHeaders.CfAccessClientSecret, serviceToken.ClientSecret);
                }

                var resp = await http.SendAsync(request, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    logger.Info($"    api ready at {url} after {attempt} attempt(s)");
                    return null;
                }

                if (attempt == 1)
                    logger.Info($"    api probe {url} returned {(int)resp.StatusCode}; retrying");
            }
            catch (HttpRequestException) { /* edge or API may not be listening yet */ }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* per-request timeout */ }

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }
        return $"api did not return 200 at {url} within the health-check budget";
    }

    private static HttpClient CreateClient(BootstrapConfig config)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (!config.Edge.Cloudflare.Enabled && config.Edge.TlsMode != EdgeTlsMode.None)
        {
            handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
        }

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }
}
