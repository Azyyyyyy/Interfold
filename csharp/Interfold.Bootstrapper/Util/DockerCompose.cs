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
