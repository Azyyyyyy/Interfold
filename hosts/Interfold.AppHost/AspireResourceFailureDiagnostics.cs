using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Interfold.AppHost;

/// <summary>Turns opaque Aspire <c>FailedToStart</c> transitions into actionable messages by
/// inspecting Docker state and logs.</summary>
public static class AspireResourceFailureDiagnostics
{
    private static readonly Regex MountNotShared = new(
        @"not shared from the host|File Sharing",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MountSourceMissing = new(
        @"invalid mount config|bind source path does not exist|no such file or directory|cannot find the file",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OutOfMemory = new(
        @"cannot allocate memory|out of memory|OOM|Killed|memory cgroup",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Best-effort diagnosis after <paramref name="resourceName"/> failed to start.
    /// Never throws.</summary>
    public static async Task<string> DescribeAsync(
        string resourceName,
        string? repoRoot = null,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        sb.Append($"Aspire resource '{resourceName}' entered FailedToStart before reaching Running.");

        var containerName = resourceName;
        var (inspectExit, inspectOut, inspectErr) = await RunDockerAsync(
            ["inspect", "--format", "{{.State.Status}} {{.State.Error}}", containerName],
            cancellationToken).ConfigureAwait(false);

        var (logsExit, logsOut, logsErr) = await RunDockerAsync(
            ["logs", "--tail", "80", containerName],
            cancellationToken).ConfigureAwait(false);

        var blob = string.Join('\n', new[] { inspectOut, inspectErr, logsOut, logsErr });

        if (MountNotShared.IsMatch(blob))
        {
            sb.AppendLine();
            sb.Append(
                " Docker rejected a bind mount: the source path is outside Docker Desktop's " +
                "file-sharing allowlist (Settings → Resources → File sharing). Add the repo directory or drive, " +
                "then restart Docker Desktop.");
            return sb.ToString();
        }

        if (MountSourceMissing.IsMatch(blob))
        {
            sb.AppendLine();
            sb.Append(" Docker could not bind-mount the required source directory or file.");
            if (!string.IsNullOrWhiteSpace(logsErr))
            {
                sb.AppendLine();
                sb.Append(" docker stderr: ").Append(TrimForMessage(logsErr));
            }
            return sb.ToString();
        }

        if (OutOfMemory.IsMatch(blob))
        {
            sb.AppendLine();
            sb.Append(" Container exited under memory pressure.");
            return sb.ToString();
        }

        if (inspectExit == 0 && !string.IsNullOrWhiteSpace(inspectOut))
        {
            sb.AppendLine();
            sb.Append(" docker inspect: ").Append(TrimForMessage(inspectOut));
        }

        if (logsExit == 0 && !string.IsNullOrWhiteSpace(logsOut))
        {
            sb.AppendLine();
            sb.Append(" docker logs (tail): ").Append(TrimForMessage(logsOut));
        }
        else if (!string.IsNullOrWhiteSpace(logsErr))
        {
            sb.AppendLine();
            sb.Append(" docker logs stderr: ").Append(TrimForMessage(logsErr));
        }

        return sb.ToString();
    }

    private static string TrimForMessage(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 1200 ? trimmed : trimmed[^1200..];
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunDockerAsync(
        IEnumerable<string> args,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return (-1, string.Empty, "failed to spawn docker");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (proc.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
