using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Interfold.Shared.Contracts.Configuration;
using Microsoft.Data.Sqlite;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Phase 6 — runs <c>docker compose up -d</c> against the emitted compose file and waits for the
/// API's <c>/health/ready</c> endpoint to return 200 through edge-nginx (or compose healthchecks
/// when Cloudflare Tunnel keeps the origin private).
/// </summary>
internal static class LaunchPhase
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromMinutes(5);

    public static async Task RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        const string Phase = "launch";
        logger.PhaseStart(Phase);

        var composeFile = PhaseArtifactLoader.RequireComposeFileOrFail(options, logger, Phase);

        var config = await PhaseArtifactLoader.TryLoadConfigAsync(options, logger, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Launch requires a loaded bootstrap config.");

        // compose up leaves an existing tag in place. A cached ghcr image can predate
        // SQLite, so the first bootstrap refreshes registry images before starting it.
        if (options.Command == BootstrapCommand.Bootstrap)
            await PullRegistryImagesAsync(composeFile, logger, ct).ConfigureAwait(false);

        // Drop any host SQLite pool before the container bind-mounts the same file.
        if (config.UsesSqlite)
            SqliteConnection.ClearAllPools();

        // api.host.local is not the mDNS name, so map it before anything tries to open it.
        LocalNameHosts.Apply(
            config,
            logger,
            canPrompt: !options.NonInteractive && !Console.IsInputRedirected);

        await Util.DockerCompose.UpCheckedAsync(composeFile, services: null, logger, ct).ConfigureAwait(false);

        try
        {
            if (config.Edge.Cloudflare.Enabled)
            {
                await WaitForComposeHealthyAsync(composeFile, logger, ct).ConfigureAwait(false);
            }
            else
            {
                await WaitForApiHealthyAsync(config, logger, ct).ConfigureAwait(false);
            }

            logger.PhaseDone(Phase);
        }
        catch (TimeoutException)
        {
            await DumpComposeLogsAsync(composeFile, logger, ct).ConfigureAwait(false);
            logger.PhaseFail(Phase, PhaseFailureReasons.HealthTimeout);
            throw;
        }
    }

    private static async Task PullRegistryImagesAsync(string composeFile, PhaseLogger logger, CancellationToken ct)
    {
        var compose = await File.ReadAllTextAsync(composeFile, ct).ConfigureAwait(false);
        var services = ComposeRegistryPull.ServicesToPull(compose);
        if (services.Count == 0)
            return;

        logger.Info($"    docker compose pull {string.Join(' ', services)}");
        var pull = await Util.DockerCompose.PullAsync(composeFile, services, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(pull.StdOut)) logger.Info(pull.StdOut.Trim());
        if (!string.IsNullOrWhiteSpace(pull.StdErr)) logger.Info(pull.StdErr.Trim());
        if (pull.ExitCode != 0)
        {
            logger.PhaseFail("launch", PhaseFailureReasons.PullFailed);
            throw new InvalidOperationException(
                $"docker compose pull exited {pull.ExitCode}: {pull.StdErr.Trim()}");
        }
    }

    private static async Task WaitForApiHealthyAsync(BootstrapConfig config, PhaseLogger logger, CancellationToken ct)
    {
        var readyUrl = ApiReadinessProbe.ResolveReadyUrl(config);
        logger.Info($"    polling {readyUrl} (up to {HealthTimeout.TotalMinutes:F0}m)");

        var deadline = DateTime.UtcNow + HealthTimeout;
        var err = await ApiReadinessProbe.TryWaitUntilAsync(config, deadline, logger, ct).ConfigureAwait(false);
        if (err is not null)
        {
            logger.Error(err);
            throw new TimeoutException(err);
        }
    }

    private static async Task WaitForComposeHealthyAsync(string composeFile, PhaseLogger logger, CancellationToken ct)
    {
        logger.Info($"    waiting for compose services healthy (edge-nginx, cloudflared; up to {HealthTimeout.TotalMinutes:F0}m)");
        var deadline = DateTime.UtcNow + HealthTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var ps = await ProcessRunner.RunAsync(
                "docker",
                ["compose", "-f", composeFile, "ps", "--format", "{{.Service}} {{.Health}} {{.State}}"],
                ct: ct).ConfigureAwait(false);
            if (ps.ExitCode == 0)
            {
                var lines = ps.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var edgeOk = lines.Any(l =>
                    l.StartsWith(ComposeServices.EdgeNginx, StringComparison.Ordinal)
                    && (l.Contains("healthy", StringComparison.OrdinalIgnoreCase)
                        || l.Contains(" running", StringComparison.OrdinalIgnoreCase)
                        || l.EndsWith("running", StringComparison.OrdinalIgnoreCase)));
                var cfdOk = lines.Any(l =>
                    l.StartsWith(ComposeServices.Cloudflared, StringComparison.Ordinal)
                    && l.Contains("running", StringComparison.OrdinalIgnoreCase));
                if (edgeOk && cfdOk)
                {
                    logger.Info("    edge-nginx and cloudflared are up");
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"edge-nginx/cloudflared did not become ready within {HealthTimeout.TotalMinutes:F0} minutes.");
    }

    private static async Task DumpComposeLogsAsync(string composeFile, PhaseLogger logger, CancellationToken ct)
    {
        logger.Warn("dumping compose logs for diagnosis...");
        await ComposeLogDumper.DumpAsync(composeFile, services: null, tailLines: 200, logger, ct).ConfigureAwait(false);
    }
}
