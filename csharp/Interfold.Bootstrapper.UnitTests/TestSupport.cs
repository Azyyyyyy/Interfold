using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using TUnit.Core.Exceptions;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Shared helpers for the unit-test project.
/// </summary>
internal static class TestSupport
{
    /// <summary>
    /// True when the test process is running inside a CI environment.
    ///
    /// Uses the de-facto standard <c>CI</c> env var (set to <c>true</c> by GitHub Actions,
    /// GitLab, CircleCI, Travis, Buildkite, etc). Tests that prefer SKIP over FAIL when a
    /// prerequisite artifact is missing should still FAIL in CI — a silent skip there masks
    /// a real workflow regression (e.g. a publish/stage step that broke without anyone
    /// noticing because the test report still says "0 failed").
    /// </summary>
    public static bool IsRunningInCi =>
        string.Equals(
            Environment.GetEnvironmentVariable("CI"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the path to the bootstrapper assembly (so <c>dotnet &lt;path&gt;</c> can launch it
    /// without needing to publish first). If the build artifact isn't on disk yet, throws a
    /// <see cref="SkipTestException"/> so the test is reported as skipped rather than failed —
    /// this happens when the unit project is run before the bootstrapper has been compiled.
    /// </summary>
    public static string BootstrapperBinaryOrSkip()
    {
        var path = LocateBootstrapperAssembly();
        if (path is null || !File.Exists(path))
        {
            throw new SkipTestException(
                "Bootstrapper assembly not found on disk; build Interfold.Bootstrapper first " +
                "or run via `dotnet test` from the solution root.");
        }
        return path;
    }

    /// <summary>
    /// Walks up from the test assembly's location to <c>csharp/Interfold.Bootstrapper/bin/</c>
    /// looking for a Debug or Release artifact. We try both because contributors may have only
    /// built one configuration locally and CI builds Release.
    /// </summary>
    public static string? LocateBootstrapperAssembly()
    {
        // AppContext.BaseDirectory is .../Interfold.Bootstrapper.UnitTests/bin/<config>/net10.0/
        // Climb up to the csharp/ root, then drop into Interfold.Bootstrapper/bin.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.Name != "csharp")
        {
            dir = dir.Parent;
        }
        if (dir is null) return null;

        // Prefer the same configuration the tests were built with, fall back to the other.
        string[] candidates =
        [
            Path.Combine(dir.FullName, "Interfold.Bootstrapper", "bin", "Debug", "net10.0", "interfold-bootstrap.dll"),
            Path.Combine(dir.FullName, "Interfold.Bootstrapper", "bin", "Release", "net10.0", "interfold-bootstrap.dll"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
    public static BootstrapOptions MakeOptions(string[]? updateServices = null)
    {
        return new BootstrapOptions(
            Command: BootstrapCommand.UpdateImages,
            ConfigPath: null,
            OutputDir: System.IO.Path.GetFullPath("./deploy"),
            SkipPrereqs: false,
            RotateSecrets: false,
            RotateCerts: false,
            NonInteractive: false,
            FaultInject: null,
            PrintPhaseStatus: false,
            UpdateServices: updateServices);
    }

    /// <summary>
    /// Builds a fresh <see cref="BootstrapConfig"/> with explicit deployment values so the
    /// derivation inputs are unambiguous, default-shipped ports (5001/8080/8081), and an empty
    /// <see cref="ApiRuntimeSection"/> so every test starts from the "needs derivation" state.
    /// </summary>
    public static BootstrapConfig MakeConfigWithEmptyApiRuntime(
        bool webHttps = false,
        params string[] hosts)
    {
        var cfg = new BootstrapConfig
        {
            Deployment =
            {
                Hosts = hosts.Length > 0 ? [.. hosts] : ["api.example.com"],
                WebHttps = webHttps,
            },
        };
        cfg.ApiRuntime.CallbackBaseUrl = string.Empty;
        cfg.ApiRuntime.JwtAuthority = string.Empty;
        cfg.ApiRuntime.CorsAllowedOrigins = [];
        return cfg;
    }

    /// <summary>
    /// Represents a temporary scratch directory that is automatically deleted when disposed.
    /// </summary>
    public sealed class ScratchDir : IDisposable
    {
        public string Path { get; }

        public ScratchDir(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            TryDeleteDir(Path);
        }
    }

    public static ScratchDir NewScratchDir(string prefix) => new(prefix);

    public static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
