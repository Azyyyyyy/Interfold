using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
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

    /// <summary>Public URL for polling readiness through edge-nginx.</summary>
    internal static string ResolveReadyUrl(BootstrapConfig config)
    {
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
        BootstrapConfig config, DateTime deadline, PhaseLogger logger, CancellationToken ct)
    {
        using var http = CreateClient(config);
        var url = ResolveReadyUrl(config);
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    logger.Info($"    api ready at {url} after {attempt} attempt(s)");
                    return null;
                }
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
        if (config.Edge.TlsMode == EdgeTlsMode.None)
            return new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }
}
