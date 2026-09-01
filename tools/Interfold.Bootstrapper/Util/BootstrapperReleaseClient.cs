using System.Formats.Tar;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Interfold.Bootstrapper.Configuration;

namespace Interfold.Bootstrapper.Util;

internal sealed class BootstrapperReleaseClient : IDisposable
{
    internal const string GitHubOwner = "Azyyyyyy";
    internal const string GitHubRepo = "Interfold";

    private readonly HttpClient _http;

    internal BootstrapperReleaseClient(HttpClient http)
    {
        _http = http;
    }

    public void Dispose() => _http.Dispose();

    internal static BootstrapperReleaseClient CreateDefault()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(BootstrapperVersion.UserAgent);
        return new BootstrapperReleaseClient(http);
    }

    internal Uri TarballUrl(BootstrapperReleaseChannel channel, string rid)
        => ReleaseAssetUri(channel, BootstrapperRid.TarballAssetName(rid));

    internal Uri ChecksumsUrl(BootstrapperReleaseChannel channel)
        => ReleaseAssetUri(channel, "SHA256SUMS");

    internal Uri VersionUrl(BootstrapperReleaseChannel channel)
        => ReleaseAssetUri(channel, "version.txt");

    private static Uri ReleaseAssetUri(BootstrapperReleaseChannel channel, string fileName)
    {
        var tag = channel.ToGitHubReleaseTag();
        var overrideBase = Environment.GetEnvironmentVariable("INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL");
        if (!string.IsNullOrWhiteSpace(overrideBase))
        {
            return new Uri($"{overrideBase.TrimEnd('/')}/{tag}/{fileName}");
        }

        return new Uri(
            $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/download/{tag}/{fileName}");
    }

    internal async Task<string> FetchRemoteVersionAsync(BootstrapperReleaseChannel channel, CancellationToken ct)
    {
        var bytes = await DownloadBytesAsync(VersionUrl(channel), ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes).Trim();
    }

    internal async Task DownloadVerifiedTarballAsync(
        BootstrapperReleaseChannel channel,
        string rid,
        string destinationPath,
        CancellationToken ct)
    {
        var tarballName = BootstrapperRid.TarballAssetName(rid);
        var sumsText = Encoding.UTF8.GetString(
            await DownloadBytesAsync(ChecksumsUrl(channel), ct).ConfigureAwait(false));
        var expectedHash = ParseSha256Sum(sumsText, tarballName);

        var tarballBytes = await DownloadBytesAsync(TarballUrl(channel, rid), ct).ConfigureAwait(false);
        var actualHash = Convert.ToHexString(SHA256.HashData(tarballBytes)).ToLowerInvariant();
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SHA256 mismatch for {tarballName}: expected {expectedHash}, got {actualHash}.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllBytesAsync(destinationPath, tarballBytes, ct).ConfigureAwait(false);
    }

    internal static void ExtractBootstrapperFromTarball(string tarballPath, string destinationBinaryPath)
    {
        using var fileStream = File.OpenRead(tarballPath);
        using var gzip = new System.IO.Compression.GZipStream(
            fileStream,
            System.IO.Compression.CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.DataStream is null || entry.EntryType != TarEntryType.RegularFile)
            {
                continue;
            }

            var parent = Path.GetDirectoryName(destinationBinaryPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            using var output = File.Create(destinationBinaryPath);
            entry.DataStream.CopyTo(output);
            return;
        }

        throw new InvalidOperationException($"Tarball {tarballPath} contains no file entries.");
    }

    private async Task<byte[]> DownloadBytesAsync(Uri url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to download {url} (HTTP {(int)response.StatusCode} {response.ReasonPhrase}).");
        }

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    internal static string ParseSha256Sum(string sumsText, string fileName)
    {
        foreach (var rawLine in sumsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = rawLine;
            var space = line.IndexOf(' ');
            if (space <= 0)
            {
                continue;
            }

            var hash = line[..space].Trim();
            var name = line[(space + 1)..].Trim();
            if (name == fileName)
            {
                return hash;
            }
        }

        throw new InvalidOperationException($"SHA256SUMS does not contain an entry for {fileName}.");
    }
}
