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
    // A missing name will not appear later in the same launch. Connection refused still
    // retries for the full budget, because nginx comes up after the containers start.
    private const int NameResolutionAttempts = 3;

    /// <summary>Public API origin plus <c>/health/ready</c>. Same address a browser opens.</summary>
    internal static string ResolveReadyUrl(BootstrapConfig config)
    {
        var origin = ConfigPhase.FormatPublicApiOrigin(config);
        if (string.IsNullOrEmpty(origin))
        {
            throw new InvalidOperationException(
                "No public API address is configured for the readiness probe.");
        }

        return origin + HealthEndpoints.Ready;
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
        var nameMisses = 0;
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
            catch (HttpRequestException ex) when (IsNameResolutionFailure(ex))
            {
                nameMisses++;
                if (nameMisses >= NameResolutionAttempts)
                {
                    return $"{url} did not resolve. The health check uses the address people open.";
                }

                if (nameMisses == 1)
                    logger.Info($"    api probe {url} did not resolve; retrying");
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

    private static bool IsNameResolutionFailure(HttpRequestException ex)
    {
        if (ex.HttpRequestError == HttpRequestError.NameResolutionError)
            return true;

        return ex.InnerException is System.Net.Sockets.SocketException sock
            && sock.SocketErrorCode is System.Net.Sockets.SocketError.HostNotFound
                or System.Net.Sockets.SocketError.NoData;
    }
}
