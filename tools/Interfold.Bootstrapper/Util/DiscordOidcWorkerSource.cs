using System.Net.Http.Headers;
using System.Text.Json;
using Interfold.Bootstrapper.Cli;

namespace Interfold.Bootstrapper.Util;

/// <summary>
/// Downloads a pinned Erisa discord-oidc-worker commit. Tests override the fetch root or
/// supply a fixture directory — never hit live GitHub from CI.
/// </summary>
internal static class DiscordOidcWorkerSource
{
    internal const string PinnedSha = "ba7b6843b0420e61034861e4e5c02dc9d05c88d9";
    internal const string WorkerFileName = "worker.js";
    internal const string PackageFileName = "package.json";
    internal const string ConfigFileName = "config.json";
    internal const string SourceDirEnv = "INTERFOLD_DISCORD_OIDC_SOURCE_DIR";
    internal const string FetchBaseUrlEnv = "INTERFOLD_DISCORD_OIDC_FETCH_BASE_URL";

    internal const string DefaultFetchBaseUrl =
        "https://raw.githubusercontent.com/Erisa/discord-oidc-worker";

    internal static string CacheDirectory(string outputDir)
        => Path.Combine(outputDir, ".cache", "discord-oidc", PinnedSha);

    internal static string ResolveFetchBaseUrl()
    {
        var overrideBase = Environment.GetEnvironmentVariable(FetchBaseUrlEnv);
        if (string.IsNullOrWhiteSpace(overrideBase))
            return DefaultFetchBaseUrl;

        return overrideBase.Trim().TrimEnd('/');
    }

    internal static async Task<string> EnsureWorkDirectoryAsync(
        string outputDir,
        HttpClient http,
        PhaseLogger logger,
        CancellationToken ct)
    {
        var workDir = CacheDirectory(outputDir);
        Directory.CreateDirectory(workDir);

        var sourceOverride = Environment.GetEnvironmentVariable(SourceDirEnv);
        if (!string.IsNullOrWhiteSpace(sourceOverride))
        {
            CopyRequired(sourceOverride.Trim(), workDir);
            return workDir;
        }

        if (HasRequiredFiles(workDir))
            return workDir;

        var baseUrl = ResolveFetchBaseUrl();
        logger.Info($"    fetching discord-oidc-worker {PinnedSha[..12]}…");
        await DownloadFileAsync(http, FileUrl(baseUrl, WorkerFileName), Path.Combine(workDir, WorkerFileName), ct)
            .ConfigureAwait(false);
        await DownloadFileAsync(http, FileUrl(baseUrl, PackageFileName), Path.Combine(workDir, PackageFileName), ct)
            .ConfigureAwait(false);
        return workDir;
    }

    internal static void WriteConfig(string workDir, string clientId, string clientSecret, string redirectUrl)
    {
        var config = new DiscordOidcConfigJson
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            RedirectUrl = redirectUrl,
            ServersToCheckRolesFor = [],
        };
        var json = JsonSerializer.Serialize(config, CloudflareApiJsonContext.Default.DiscordOidcConfigJson);
        File.WriteAllText(Path.Combine(workDir, ConfigFileName), json);
    }

    // Script upload rejects application/json (10162). JSON-module namespace is
    // default + named keys, matching `import * as config from "./config.json"`.
    internal static string ToConfigEsModule(string json)
    {
        try
        {
            _ = JsonSerializer.Deserialize(json, CloudflareApiJsonContext.Default.DiscordOidcConfigJson)
                ?? throw new InvalidDataException("discord-oidc config.json must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("discord-oidc config.json must be a JSON object.", ex);
        }

        var destructure = string.Join(", ", DiscordOidcConfigJson.EsModuleExportNames);
        return $"const json = {json};{Environment.NewLine}export default json;{Environment.NewLine}export const {{ {destructure} }} = json;{Environment.NewLine}";
    }

    internal static string FileUrl(string baseUrl, string fileName)
        => $"{baseUrl.TrimEnd('/')}/{PinnedSha}/{fileName}";

    private static bool HasRequiredFiles(string directory)
        => File.Exists(Path.Combine(directory, WorkerFileName))
           && File.Exists(Path.Combine(directory, PackageFileName));

    private static void CopyRequired(string sourceDir, string workDir)
    {
        var worker = Path.Combine(sourceDir, WorkerFileName);
        var package = Path.Combine(sourceDir, PackageFileName);
        if (!File.Exists(worker) || !File.Exists(package))
        {
            throw new InvalidOperationException(
                $"{SourceDirEnv} must contain {WorkerFileName} and {PackageFileName}.");
        }

        File.Copy(worker, Path.Combine(workDir, WorkerFileName), overwrite: true);
        File.Copy(package, Path.Combine(workDir, PackageFileName), overwrite: true);
    }

    private static async Task DownloadFileAsync(HttpClient http, string url, string destPath, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to download Discord OIDC worker file ({(int)response.StatusCode}): {url}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length == 0)
            throw new InvalidOperationException($"Discord OIDC worker download was empty: {url}");

        await File.WriteAllBytesAsync(destPath, bytes, ct).ConfigureAwait(false);
    }

    internal static HttpClient CreateHttp()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(BootstrapperVersion.UserAgent);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }
}
