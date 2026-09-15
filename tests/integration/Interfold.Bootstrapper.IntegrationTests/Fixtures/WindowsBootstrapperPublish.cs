using System.Diagnostics;

namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// Session-wide win-x64 publish of <c>interfold-bootstrap.exe</c> for native
/// Windows install-service / publish-smoke tests. Prefer
/// <c>INTERFOLD_BOOTSTRAPPER_WIN_PUBLISH_DIR</c> when CI already published.
/// </summary>
internal static class WindowsBootstrapperPublish
{
    internal const string PublishDirEnvVar = "INTERFOLD_BOOTSTRAPPER_WIN_PUBLISH_DIR";

    public static readonly Lazy<Task<string>> PublishedExePath = new(ResolveExeAsync);

    private static async Task<string> ResolveExeAsync()
    {
        var fromEnv = Environment.GetEnvironmentVariable(PublishDirEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var envExe = Path.Combine(fromEnv, "interfold-bootstrap.exe");
            if (File.Exists(envExe))
            {
                return Path.GetFullPath(envExe);
            }
        }

        var outDir = Path.Combine(
            Path.GetTempPath(),
            "interfold-bootstrap-win-publish",
            $"run-{Environment.ProcessId}");
        Directory.CreateDirectory(outDir);

        await RunDotnetAsync(
            "publish",
            "tools/Interfold.Bootstrapper/Interfold.Bootstrapper.csproj",
            "/p:PublishProfile=win-x64",
            "-o", outDir).ConfigureAwait(false);

        var exe = Path.Combine(outDir, "interfold-bootstrap.exe");
        if (!File.Exists(exe))
        {
            throw new InvalidOperationException(
                $"win-x64 publish succeeded but {exe} is missing.");
        }

        return exe;
    }

    private static async Task RunDotnetAsync(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = RepoRoot.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet.");

        var stdOutTask = proc.StandardOutput.ReadToEndAsync();
        var stdErrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync().ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet {string.Join(' ', args)} exited {proc.ExitCode}.\n" +
                $"stdout:\n{await stdOutTask.ConfigureAwait(false)}\n" +
                $"stderr:\n{await stdErrTask.ConfigureAwait(false)}");
        }
    }
}
