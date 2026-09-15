using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interfold.Bootstrapper.Configuration;

namespace Interfold.Bootstrapper.Util;

internal sealed class BootstrapperReleaseClient : IDisposable
{
    internal const string GitHubOwner = "Azyyyyyy";
    internal const string GitHubRepo = "Interfold";
    internal const string StableRollingTagPrefix = "stable-";
    internal const string BleedingEdgeRollingTagPrefix = "bleeding-edge-";

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
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return new BootstrapperReleaseClient(http);
    }

    private static bool UsesReleaseMirror
        => !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL"));

    /// <summary>
    /// Sync URL helper for pins and <c>INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL</c> mirrors.
    /// Live rolling channels resolve a unique tag via <see cref="ResolveReleaseTagAsync"/> first.
    /// </summary>
    internal Uri ReleaseAssetUrl(BootstrapperReleaseChannel channel, string rid)
        => ReleaseAssetUri(channel.ToGitHubReleaseTag(), BootstrapperRid.ReleaseAssetName(rid));

    /// <summary>Backward-compatible alias; asset may be <c>.tar.gz</c> or <c>.zip</c>.</summary>
    internal Uri TarballUrl(BootstrapperReleaseChannel channel, string rid)
        => ReleaseAssetUrl(channel, rid);

    private static Uri ReleaseAssetUri(string tag, string fileName)
    {
        // Version stamps embed `+` build metadata in rolling tags; encode so download
        // URLs don't treat `+` as a space.
        var encodedTag = Uri.EscapeDataString(tag);
        var overrideBase = Environment.GetEnvironmentVariable("INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL");
        if (!string.IsNullOrWhiteSpace(overrideBase))
        {
            return new Uri($"{overrideBase.TrimEnd('/')}/{encodedTag}/{fileName}");
        }

        return new Uri(
            $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/download/{encodedTag}/{fileName}");
    }

    /// <summary>
    /// Resolves the GitHub Release tag for <paramref name="channel"/>.
    /// Pins use the tag as-is; rolling channels query the Releases API for the newest
    /// <c>stable-{version}</c> / <c>bleeding-edge-{version}</c> tag. Test mirrors keep using
    /// <see cref="BootstrapperReleaseChannel.ToGitHubReleaseTag"/>.
    /// </summary>
    internal async Task<string> ResolveReleaseTagAsync(
        BootstrapperReleaseChannel channel, CancellationToken ct)
    {
        if (UsesReleaseMirror)
        {
            return channel.ToGitHubReleaseTag();
        }

        if (channel.IsPinned)
        {
            return channel.ToWireValue();
        }

        if (channel == BootstrapperReleaseChannel.Stable)
        {
            return await FetchNewestRollingTagAsync(
                    StableRollingTagPrefix,
                    requirePrerelease: false,
                    ct)
                .ConfigureAwait(false);
        }

        if (channel == BootstrapperReleaseChannel.BleedingEdge)
        {
            return await FetchNewestRollingTagAsync(
                    BleedingEdgeRollingTagPrefix,
                    requirePrerelease: true,
                    ct)
                .ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Unknown bootstrapper release channel '{channel.ToWireValue()}'.");
    }

    /// <summary>
    /// Remote version identity. Live releases encode it in the tag
    /// (<c>stable-{version}</c> / <c>bleeding-edge-{version}</c> / pin <c>v*</c>);
    /// test mirrors still serve <c>version.txt</c>.
    /// </summary>
    internal async Task<string> FetchRemoteVersionAsync(BootstrapperReleaseChannel channel, CancellationToken ct)
    {
        if (channel.IsPinned)
        {
            return channel.ToWireValue();
        }

        var tag = await ResolveReleaseTagAsync(channel, ct).ConfigureAwait(false);
        if (UsesReleaseMirror)
        {
            var bytes = await DownloadBytesAsync(ReleaseAssetUri(tag, "version.txt"), ct)
                .ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes).Trim();
        }

        return VersionFromReleaseTag(tag);
    }

    /// <summary>
    /// Strips the rolling channel prefix from a live release tag. Pins return the tag as-is.
    /// </summary>
    internal static string VersionFromReleaseTag(string tag)
    {
        if (tag.StartsWith(StableRollingTagPrefix, StringComparison.Ordinal))
        {
            return tag[StableRollingTagPrefix.Length..];
        }

        if (tag.StartsWith(BleedingEdgeRollingTagPrefix, StringComparison.Ordinal))
        {
            return tag[BleedingEdgeRollingTagPrefix.Length..];
        }

        return tag;
    }

    internal async Task DownloadVerifiedReleaseAssetAsync(
        BootstrapperReleaseChannel channel,
        string rid,
        string destinationPath,
        CancellationToken ct)
    {
        var tag = await ResolveReleaseTagAsync(channel, ct).ConfigureAwait(false);
        var assetName = BootstrapperRid.ReleaseAssetName(rid);
        var expectedHash = UsesReleaseMirror
            ? await FetchMirrorSha256Async(tag, assetName, ct).ConfigureAwait(false)
            : await FetchGitHubAssetSha256Async(tag, assetName, ct).ConfigureAwait(false);

        var assetBytes = await DownloadBytesAsync(ReleaseAssetUri(tag, assetName), ct).ConfigureAwait(false);
        var actualHash = Convert.ToHexString(SHA256.HashData(assetBytes)).ToLowerInvariant();
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SHA256 mismatch for {assetName}: expected {expectedHash}, got {actualHash}.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllBytesAsync(destinationPath, assetBytes, ct).ConfigureAwait(false);
    }

    /// <summary>Backward-compatible alias for <see cref="DownloadVerifiedReleaseAssetAsync"/>.</summary>
    internal Task DownloadVerifiedTarballAsync(
        BootstrapperReleaseChannel channel,
        string rid,
        string destinationPath,
        CancellationToken ct)
        => DownloadVerifiedReleaseAssetAsync(channel, rid, destinationPath, ct);

    private async Task<string> FetchMirrorSha256Async(string tag, string assetName, CancellationToken ct)
    {
        var sumsText = Encoding.UTF8.GetString(
            await DownloadBytesAsync(ReleaseAssetUri(tag, "SHA256SUMS"), ct).ConfigureAwait(false));
        return ParseSha256Sum(sumsText, assetName);
    }

    private async Task<string> FetchGitHubAssetSha256Async(string tag, string assetName, CancellationToken ct)
    {
        var url = new Uri(
            $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/tags/{Uri.EscapeDataString(tag)}");
        using var doc = await DownloadJsonAsync(url, ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"GitHub release '{tag}' response did not include an assets array.");
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameEl)
                || !string.Equals(nameEl.GetString(), assetName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!asset.TryGetProperty("digest", out var digestEl)
                || digestEl.GetString() is not { Length: > 0 } digest)
            {
                throw new InvalidOperationException(
                    $"GitHub asset '{assetName}' on release '{tag}' has no digest (immutable releases required).");
            }

            return ParseAssetDigestSha256(digest);
        }

        throw new InvalidOperationException(
            $"GitHub release '{tag}' does not list asset '{assetName}'.");
    }

    /// <summary>Parses a Releases API <c>digest</c> value (<c>sha256:&lt;hex&gt;</c>) to lowercase hex.</summary>
    internal static string ParseAssetDigestSha256(string digest)
    {
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported asset digest '{digest}'. Expected '{prefix}<hex>'.");
        }

        var hex = digest[prefix.Length..].Trim();
        if (hex.Length == 0)
        {
            throw new InvalidOperationException($"Empty sha256 digest in '{digest}'.");
        }

        return hex.ToLowerInvariant();
    }

    /// <summary>
    /// Walks GitHub Releases pages (newest first) until a non-draft release whose tag starts
    /// with <paramref name="tagPrefix"/> matches the prerelease filter.
    /// </summary>
    private async Task<string> FetchNewestRollingTagAsync(
        string tagPrefix,
        bool requirePrerelease,
        CancellationToken ct)
    {
        const int pageSize = 100;
        for (var page = 1; ; page++)
        {
            var url = new Uri(
                $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases?per_page={pageSize}&page={page}");
            using var doc = await DownloadJsonAsync(url, ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    $"GitHub releases list response was not an array (page {page}).");
            }

            var count = doc.RootElement.GetArrayLength();
            if (count == 0)
            {
                break;
            }

            var tag = SelectNewestRollingTag(doc.RootElement, tagPrefix, requirePrerelease);
            if (tag is not null)
            {
                return tag;
            }

            if (count < pageSize)
            {
                break;
            }
        }

        var kind = requirePrerelease ? "prerelease" : "release";
        throw new InvalidOperationException(
            $"No {kind} tagged '{tagPrefix}*' was found on GitHub Releases.");
    }

    /// <summary>
    /// Picks the first non-draft release on a page (already newest-first) whose tag starts
    /// with <paramref name="tagPrefix"/> and matches <paramref name="requirePrerelease"/>.
    /// </summary>
    internal static string? SelectNewestRollingTag(
        JsonElement releases,
        string tagPrefix,
        bool requirePrerelease)
    {
        if (releases.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var release in releases.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            var isPrerelease = release.TryGetProperty("prerelease", out var pre)
                && pre.ValueKind == JsonValueKind.True;
            if (isPrerelease != requirePrerelease)
            {
                continue;
            }

            if (!release.TryGetProperty("tag_name", out var tagEl)
                || tagEl.GetString() is not { Length: > 0 } tag)
            {
                continue;
            }

            if (tag.StartsWith(tagPrefix, StringComparison.Ordinal))
            {
                return tag;
            }
        }

        return null;
    }

    private async Task<JsonDocument> DownloadJsonAsync(Uri url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to query {url} (HTTP {(int)response.StatusCode} {response.ReasonPhrase}).");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    internal static void ExtractBootstrapperFromArchive(string archivePath, string destinationBinaryPath)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ExtractBootstrapperFromZip(archivePath, destinationBinaryPath);
            return;
        }

        ExtractBootstrapperFromTarball(archivePath, destinationBinaryPath);
    }

    internal static void ExtractBootstrapperFromTarball(string tarballPath, string destinationBinaryPath)
    {
        using var fileStream = File.OpenRead(tarballPath);
        using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.DataStream is null || entry.EntryType != TarEntryType.RegularFile)
            {
                continue;
            }

            WriteBinaryStream(entry.DataStream, destinationBinaryPath);
            return;
        }

        throw new InvalidOperationException($"Tarball {tarballPath} contains no file entries.");
    }

    internal static void ExtractBootstrapperFromZip(string zipPath, string destinationBinaryPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var preferredName = Path.GetFileName(destinationBinaryPath);
        ZipArchiveEntry? chosen = null;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/'))
            {
                continue;
            }

            if (string.Equals(entry.Name, preferredName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Name, HostPaths.UnixFileName, StringComparison.Ordinal)
                || string.Equals(entry.Name, HostPaths.WindowsFileName, StringComparison.OrdinalIgnoreCase))
            {
                chosen = entry;
                break;
            }

            chosen ??= entry;
        }

        if (chosen is null)
        {
            throw new InvalidOperationException($"Zip {zipPath} contains no file entries.");
        }

        using var stream = chosen.Open();
        WriteBinaryStream(stream, destinationBinaryPath);
    }

    private static void WriteBinaryStream(Stream source, string destinationBinaryPath)
    {
        var parent = Path.GetDirectoryName(destinationBinaryPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        using var output = File.Create(destinationBinaryPath);
        source.CopyTo(output);
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

    /// <summary>Parses a classic <c>sha256sum</c> file (test mirrors only).</summary>
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
            if (name.StartsWith('*'))
            {
                name = name[1..];
            }

            if (name == fileName)
            {
                return hash;
            }
        }

        throw new InvalidOperationException($"SHA256SUMS does not contain an entry for {fileName}.");
    }
}
