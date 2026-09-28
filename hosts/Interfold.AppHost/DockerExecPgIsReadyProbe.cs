using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Interfold.AppHost;

/// <summary>Runs <c>pg_isready</c> inside the Postgres container via <c>docker exec</c>.
/// Feeds the msg-db health check when DB endpoints are internal-only (no host port to
/// TCP-probe).</summary>
internal static class DockerExecPgIsReadyProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private const string ReadinessScript =
        $"pg_isready -U \"${ContainerEnvNames.PostgresUser}\" -d postgres >/dev/null 2>&1";

    public static async Task<HealthCheckResult> RunAsync(string resourceName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return HealthCheckResult.Unhealthy("resource name is empty");
        }

        var (containerName, lookupError) = await DockerExecCqlProbe.ResolveContainerNameAsync(resourceName, cancellationToken).ConfigureAwait(false);
        if (containerName is null)
        {
            return HealthCheckResult.Unhealthy(lookupError ?? "container not yet allocated");
        }

        return await RunExecProbeAsync(containerName, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HealthCheckResult> RunExecProbeAsync(string containerName, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(containerName);
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(ReadinessScript);

        var (exit, stdout, stderr) = await DockerExecCqlProbe.RunDockerProcessAsync(psi, cancellationToken).ConfigureAwait(false);
        if (exit == 0)
        {
            return HealthCheckResult.Healthy();
        }
        if (exit == -1)
        {
            return HealthCheckResult.Unhealthy(stderr);
        }

        var detail = stderr.Length > 0 ? stderr : stdout;
        var trimmed = detail.Length > 256 ? detail[..256] : detail;
        return HealthCheckResult.Unhealthy($"docker exec exit={exit}: {trimmed.Trim()}");
    }
}
