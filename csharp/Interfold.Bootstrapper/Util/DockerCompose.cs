using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Interfold.Bootstrapper.Util;

internal static class DockerCompose
{
    public static async Task<ProcessRunResult> UpAsync(
        string composeFile,
        IReadOnlyList<string>? services = null,
        bool detach = true,
        bool build = false,
        CancellationToken ct = default)
    {
        var args = new List<string> { "compose", "-f", composeFile, "up" };
        if (detach) args.Add("-d");
        if (build) args.Add("--build");
        if (services is not null) args.AddRange(services);
        return await ProcessRunner.RunAsync("docker", args, ct: ct).ConfigureAwait(false);
    }

    public static async Task<ProcessRunResult> PullAsync(
        string composeFile,
        IReadOnlyList<string>? services = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "compose", "-f", composeFile, "pull" };
        if (services is not null) args.AddRange(services);
        return await ProcessRunner.RunAsync("docker", args, ct: ct).ConfigureAwait(false);
    }

    public static async Task<ProcessRunResult> StopAsync(
        string composeFile,
        IReadOnlyList<string>? services = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "compose", "-f", composeFile, "stop" };
        if (services is not null) args.AddRange(services);
        return await ProcessRunner.RunAsync("docker", args, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts previously created compose services. Sister of <see cref="StopAsync"/> — the
    /// "start what we stopped" leg of a stop/start pair. Does NOT create containers; pair
    /// with <see cref="UpAsync"/> when the container may not exist yet.
    /// </summary>
    public static async Task<ProcessRunResult> StartAsync(
        string composeFile,
        IReadOnlyList<string>? services = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "compose", "-f", composeFile, "start" };
        if (services is not null) args.AddRange(services);
        return await ProcessRunner.RunAsync("docker", args, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>docker compose down</c>. When <paramref name="removeVolumes"/> is true,
    /// adds <c>-v</c> so named volumes are also removed (destructive — only reachable via
    /// operator opt-in paths).
    /// </summary>
    public static async Task<ProcessRunResult> DownAsync(
        string composeFile,
        IReadOnlyList<string>? services = null,
        bool removeVolumes = false,
        CancellationToken ct = default)
    {
        var args = new List<string> { "compose", "-f", composeFile, "down" };
        if (removeVolumes) args.Add("-v");
        if (services is not null) args.AddRange(services);
        return await ProcessRunner.RunAsync("docker", args, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>docker compose ps</c>. Defaults to <c>-q</c> (container id only), which is
    /// what every current caller wants (they parse stdout for the id and pipe it to
    /// <c>docker cp</c> / <c>docker inspect</c>). Set <paramref name="includeStopped"/> to
    /// add <c>-a</c> so stopped-but-created containers are also returned — required for the
    /// restore path which needs a handle on a container it just <c>stop</c>'d.
    /// </summary>
    public static async Task<ProcessRunResult> PsAsync(
        string composeFile,
        string? service = null,
        bool quietIds = true,
        bool includeStopped = false,
        CancellationToken ct = default)
    {
        var args = new List<string> { "compose", "-f", composeFile, "ps" };
        if (includeStopped) args.Add("-a");
        if (quietIds) args.Add("-q");
        if (service is not null) args.Add(service);
        return await ProcessRunner.RunAsync("docker", args, ct: ct).ConfigureAwait(false);
    }

    public static async Task<ProcessRunResult> LogsAsync(
        string composeFile,
        IReadOnlyList<string>? services = null,
        bool follow = false,
        int? tail = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "compose", "-f", composeFile, "logs" };
        if (follow) args.Add("-f");
        if (tail is not null) args.Add($"--tail={tail}");
        if (services is not null) args.AddRange(services);
        return await ProcessRunner.RunAsync("docker", args, ct: ct).ConfigureAwait(false);
    }

    public static Task<ProcessRunResult> ExecAsync(
        string composeFile,
        string service,
        string command,
        IReadOnlyList<string>? args = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? stdin = null,
        CancellationToken ct = default)
    {
        return DockerComposeExec.RunAsync(composeFile, service, command, args, env, stdin, ct);
    }
}
