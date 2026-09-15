using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>Host-side scratch for native Windows bootstrapper tests (no DinD).</summary>
internal sealed class WindowsInstallScratch : IAsyncDisposable
{
    private WindowsInstallScratch(string root, string outputDir, string configPath, string binaryPath)
    {
        Root = root;
        OutputDir = outputDir;
        ConfigPath = configPath;
        BinaryPath = binaryPath;
        TaskXmlDir = Path.Combine(root, "scheduled-tasks");
    }

    public string Root { get; }
    public string OutputDir { get; }
    public string ConfigPath { get; }
    public string BinaryPath { get; }
    public string TaskXmlDir { get; }

    public static async Task<WindowsInstallScratch> CreateAsync(string testName, CancellationToken ct = default)
    {
        var binaryPath = await WindowsBootstrapperPublish.PublishedExePath.Value.ConfigureAwait(false);
        var root = Path.Combine(
            Path.GetTempPath(),
            "interfold-win-install-tests",
            $"{Sanitize(testName)}-{Guid.NewGuid():N}");
        var outputDir = Path.Combine(root, "deploy");
        Directory.CreateDirectory(outputDir);

        var configPath = Path.Combine(outputDir, "interfold.bootstrap.json");
        var template = await File.ReadAllTextAsync(TestConfigPaths.DefaultConfig, ct).ConfigureAwait(false);
        var rootNode = JsonNode.Parse(template)
            ?? throw new InvalidOperationException("test bootstrap config parsed to null");
        var deployment = rootNode["deployment"] as JsonObject ?? new JsonObject();
        deployment["outputDir"] = outputDir;
        rootNode["deployment"] = deployment;
        await File.WriteAllTextAsync(
            configPath,
            rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            ct).ConfigureAwait(false);

        return new WindowsInstallScratch(root, outputDir, configPath, binaryPath);
    }

    public async Task SetUpdateEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(ConfigPath, ct).ConfigureAwait(false);
        var root = JsonNode.Parse(json)
            ?? throw new InvalidOperationException("config parsed to null");
        var deployment = root["deployment"] as JsonObject ?? new JsonObject();
        var update = deployment["update"] as JsonObject ?? new JsonObject();
        update["enabled"] = enabled;
        deployment["update"] = update;
        root["deployment"] = deployment;
        await File.WriteAllTextAsync(
            ConfigPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            ct).ConfigureAwait(false);
    }

    public async Task SetBackupScheduleAsync(string schedule, CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(ConfigPath, ct).ConfigureAwait(false);
        var root = JsonNode.Parse(json)
            ?? throw new InvalidOperationException("config parsed to null");
        var deployment = root["deployment"] as JsonObject ?? new JsonObject();
        var backup = deployment["backup"] as JsonObject ?? new JsonObject();
        backup["schedule"] = schedule;
        backup["enabled"] = true;
        deployment["backup"] = backup;
        root["deployment"] = deployment;
        await File.WriteAllTextAsync(
            ConfigPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            ct).ConfigureAwait(false);
    }

    public async Task<(int ExitCode, string Stdout, string Stderr)> RunInstallServiceAsync(
        params string[] extraArgs)
    {
        var args = new List<string>
        {
            "install-service",
            "--config", ConfigPath,
            "--output-dir", OutputDir,
            "--non-interactive",
            "--binary-path", BinaryPath,
        };
        args.AddRange(extraArgs);
        return await RunBinaryAsync(args).ConfigureAwait(false);
    }

    public Task<(int ExitCode, string Stdout, string Stderr)> RunPublishAsync(params string[] extraArgs)
    {
        var args = new List<string>
        {
            "publish",
            "--config", ConfigPath,
            "--output-dir", OutputDir,
            "--non-interactive",
            "--skip-prereqs",
        };
        args.AddRange(extraArgs);
        return RunBinaryAsync(args);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunBinaryAsync(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = BinaryPath,
            WorkingDirectory = OutputDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {BinaryPath}.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync().ConfigureAwait(false);
        return (proc.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    public static async Task<(int ExitCode, string Stdout, string Stderr)> SchtasksAsync(
        params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start schtasks.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync().ConfigureAwait(false);
        return (proc.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    public static Task DeleteTaskIfPresentAsync(string taskName)
        => SchtasksAsync("/Delete", "/TN", taskName, "/F");

    public static async Task<string> ReadUtf16Async(string path)
    {
        // Task XML is written as Encoding.Unicode (UTF-16 LE).
        return await File.ReadAllTextAsync(path, Encoding.Unicode).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
            // best-effort scratch cleanup
        }

        return ValueTask.CompletedTask;
    }

    private static string Sanitize(string testName)
    {
        var sb = new StringBuilder(testName.Length);
        foreach (var c in testName)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            {
                sb.Append(c);
            }
        }

        return sb.Length == 0 ? "scratch" : sb.ToString();
    }
}
