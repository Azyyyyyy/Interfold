using System.Text.Json;
using Interfold.Bootstrapper.Cli;

namespace Interfold.Bootstrapper.Util;

/// <summary>
/// Bundles Erisa's worker.js with hono/jose, leaving <c>./config.json</c> as a separate module.
/// Tests can skip npm via <see cref="BundlePathEnv"/>.
/// </summary>
internal static class DiscordOidcWorkerBundler
{
    internal const string BundleFileName = "worker.bundle.js";
    internal const string BundlePathEnv = "INTERFOLD_DISCORD_OIDC_BUNDLE_PATH";

    internal static async Task<string> BundleAsync(
        string workDir,
        PhaseLogger logger,
        CancellationToken ct)
    {
        var overrideBundle = Environment.GetEnvironmentVariable(BundlePathEnv);
        if (!string.IsNullOrWhiteSpace(overrideBundle))
        {
            var path = overrideBundle.Trim();
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"{BundlePathEnv} points at a missing file: {path}");
            }

            return path;
        }

        if (!await ProcessRunner.ExistsOnPathAsync("npm", ct).ConfigureAwait(false)
            || !await ProcessRunner.ExistsOnPathAsync("npx", ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Discord Access needs Node.js (npm and npx on PATH) to bundle discord-oidc-worker. " +
                "Install Node, or unset api.oauth.discordClientId to keep Google-only Access.");
        }

        var versions = ReadRuntimeDependencyVersions(workDir);
        logger.Info($"    bundling discord-oidc-worker (hono {versions.Hono}, jose {versions.Jose})");

        var install = await ProcessRunner.RunAsync(
                "npm",
                ["install", "--no-fund", "--no-audit", $"hono@{versions.Hono}", $"jose@{versions.Jose}"],
                workingDirectory: workDir,
                ct: ct)
            .ConfigureAwait(false);
        if (install.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"npm install failed for discord-oidc-worker (exit {install.ExitCode}): {TrimOutput(install.StdErr)}");
        }

        var bundlePath = Path.Combine(workDir, BundleFileName);
        var esbuild = await ProcessRunner.RunAsync(
                "npx",
                [
                    "--yes",
                    "esbuild",
                    DiscordOidcWorkerSource.WorkerFileName,
                    "--bundle",
                    "--format=esm",
                    "--external:./config.json",
                    $"--outfile={BundleFileName}",
                ],
                workingDirectory: workDir,
                ct: ct)
            .ConfigureAwait(false);
        if (esbuild.ExitCode != 0 || !File.Exists(bundlePath))
        {
            throw new InvalidOperationException(
                $"esbuild failed for discord-oidc-worker (exit {esbuild.ExitCode}): {TrimOutput(esbuild.StdErr)}");
        }

        return bundlePath;
    }

    internal static (string Hono, string Jose) ReadRuntimeDependencyVersions(string workDir)
    {
        var packagePath = Path.Combine(workDir, DiscordOidcWorkerSource.PackageFileName);
        var json = File.ReadAllText(packagePath);
        var package = JsonSerializer.Deserialize(json, CloudflareApiJsonContext.Default.DiscordOidcPackageJson)
            ?? throw new InvalidOperationException("discord-oidc-worker package.json did not deserialize.");

        if (!package.Dependencies.TryGetValue("hono", out var hono)
            || !package.Dependencies.TryGetValue("jose", out var jose)
            || string.IsNullOrWhiteSpace(hono)
            || string.IsNullOrWhiteSpace(jose))
        {
            throw new InvalidOperationException(
                "discord-oidc-worker package.json is missing hono/jose dependency versions.");
        }

        return (hono.Trim(), jose.Trim());
    }

    private static string TrimOutput(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }
}
