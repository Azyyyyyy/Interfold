using Interfold.DatabaseBootstrap;
using Interfold.Bootstrapper.Phases;
using Interfold.Bootstrapper.Cli;

namespace Interfold.Bootstrapper.Util;

internal static class PostgresReadinessProbe
{
    /// <summary>
    /// Convenience overload for callers whose readiness policy is "first successful probe
    /// = ready" (no 3-in-a-row confirmation). Used by <c>RestorePhase</c>, whose
    /// original 5-minute deadline predates the consecutive-success check that landed in
    /// <c>DatabaseInitPhase</c>: restore already owns a live cluster, so a single
    /// <c>pg_isready</c> success is a sufficient handoff signal and staying with 3 would
    /// add ~4s to every restore.
    /// </summary>
    public static Task WaitAsync(
        string composeFile,
        string service,
        TimeSpan timeout,
        PhaseLogger logger,
        CancellationToken ct,
        string? runAsRole = null) =>
        WaitAsync(
            composeFile,
            service,
            new PostgresReadinessOptions(timeout, RequiredConsecutiveSuccesses: 1, RunAsRole: runAsRole),
            logger,
            ct);

    public static async Task WaitAsync(
        string composeFile,
        string service,
        PostgresReadinessOptions options,
        PhaseLogger logger,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(options.Timeout);
        var attempt = 0;
        var consecutiveSuccesses = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            
            var args = new List<string> { "compose", "-f", composeFile, "exec", "-T", service, "pg_isready", "-h", "127.0.0.1", "-p", "5432" };
            if (!string.IsNullOrWhiteSpace(options.RunAsRole))
            {
                args.Add("-U");
                args.Add(options.RunAsRole);
            }

            var probe = await ProcessRunner.RunAsync("docker", args.ToArray(), ct: ct).ConfigureAwait(false);
            
            if (probe.ExitCode == 0)
            {
                consecutiveSuccesses++;
                if (consecutiveSuccesses >= options.RequiredConsecutiveSuccesses)
                {
                    logger.Info(
                        $"    postgres ready after {attempt} probe(s) ({options.RequiredConsecutiveSuccesses} consecutive TCP pg_isready, normal mode confirmed)");
                    return;
                }
            }
            else if (consecutiveSuccesses > 0)
            {
                consecutiveSuccesses = 0;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"{service} did not become ready within {options.Timeout.TotalMinutes} minutes.");
    }
}
