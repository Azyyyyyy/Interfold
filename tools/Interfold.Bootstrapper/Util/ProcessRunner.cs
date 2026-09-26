using System.Diagnostics;
using System.Text;

namespace Interfold.Bootstrapper.Util;

internal sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Lightweight wrapper over <see cref="Process"/> with stdout/stderr capture, timeout, and an
/// optional stdin payload. Used by phases that shell out to <c>apt</c>, <c>dnf</c>,
/// <c>docker</c>, <c>update-ca-certificates</c>, etc.
/// </summary>
internal static class ProcessRunner
{
    public static async Task<ProcessRunResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IDictionary<string, string?>? environment = null,
        string? stdin = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveExecutable(fileName),
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }
        if (environment is not null)
        {
            foreach (var kvp in environment)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        return new ProcessRunResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    // UseShellExecute=false skips PATHEXT, so npm/npx must become npm.cmd / npx.cmd.
    internal static string ResolveExecutable(string fileName)
    {
        if (!OperatingSystem.IsWindows()
            || Path.IsPathRooted(fileName)
            || Path.GetExtension(fileName).Length > 0)
        {
            return fileName;
        }

        return PathLookup.TryFind(
                fileName,
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("PATHEXT"),
                windows: true)
            ?? fileName;
    }

    public static async Task<bool> ExistsOnPathAsync(string command, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return PathLookup.ExistsOnWindowsPath(command);
        }

        try
        {
            var which = await RunAsync("/bin/sh", ["-c", $"command -v {command}"], ct: ct).ConfigureAwait(false);
            return which.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
