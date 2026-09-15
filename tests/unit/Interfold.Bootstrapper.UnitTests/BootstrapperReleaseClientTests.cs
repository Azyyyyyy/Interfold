using System.IO.Compression;
using System.Text.Json;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

[NotInParallel("bootstrapper-release-env")]
public sealed class BootstrapperReleaseClientTests
{
    [Test]
    public async Task PinTagTarballUrlUsesImmutableReleaseTag()
    {
        Environment.SetEnvironmentVariable("INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL", null);
        using var client = BootstrapperReleaseClient.CreateDefault();
        var pin = BootstrapperReleaseChannel.ParseWire("bootstrap-v0.0.1");
        var url = client.TarballUrl(pin, "linux-x64");
        await Assert.That(url.ToString())
            .IsEqualTo("https://github.com/Azyyyyyy/Interfold/releases/download/bootstrap-v0.0.1/interfold-bootstrap-linux-x64.tar.gz");
    }

    [Test]
    public async Task FetchRemoteVersionReturnsPinSemVerCore()
    {
        using var client = BootstrapperReleaseClient.CreateDefault();
        var pin = BootstrapperReleaseChannel.ParseWire("bootstrap-v0.0.1");
        await Assert.That(await client.FetchRemoteVersionAsync(pin, CancellationToken.None))
            .IsEqualTo("0.0.1");
    }

    [Test]
    public async Task VersionFromReleaseTagStripsRollingPrefixes()
    {
        await Assert.That(BootstrapperReleaseClient.VersionFromReleaseTag("stable-0.0.1+abc1234"))
            .IsEqualTo("0.0.1+abc1234");
        await Assert.That(BootstrapperReleaseClient.VersionFromReleaseTag("bleeding-edge-0.0.1+deadbee"))
            .IsEqualTo("0.0.1+deadbee");
        await Assert.That(BootstrapperReleaseClient.VersionFromReleaseTag("bootstrap-v0.0.1"))
            .IsEqualTo("0.0.1");
    }

    [Test]
    public async Task ParseAssetDigestSha256AcceptsSha256Prefix()
    {
        var hash = BootstrapperReleaseClient.ParseAssetDigestSha256(
            "sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");
        await Assert.That(hash)
            .IsEqualTo("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");
    }

    [Test]
    public async Task ParseAssetDigestSha256RejectsUnknownAlgorithm()
    {
        await Assert.That(() => BootstrapperReleaseClient.ParseAssetDigestSha256("md5:deadbeef"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task SelectNewestRollingTagStableSkipsPrereleasesAndDrafts()
    {
        using var doc = JsonDocument.Parse(
            """
            [
              { "tag_name": "bleeding-edge-0.0.1+ffff", "draft": false, "prerelease": true },
              { "tag_name": "stable-0.0.1+draft", "draft": true, "prerelease": false },
              { "tag_name": "v1.0.0", "draft": false, "prerelease": false },
              { "tag_name": "stable-0.0.1+cafebabe", "draft": false, "prerelease": false },
              { "tag_name": "stable-0.0.1+older", "draft": false, "prerelease": false }
            ]
            """);

        var tag = BootstrapperReleaseClient.SelectNewestRollingTag(
            doc.RootElement,
            BootstrapperReleaseClient.StableRollingTagPrefix,
            requirePrerelease: false);
        await Assert.That(tag).IsEqualTo("stable-0.0.1+cafebabe");
    }

    [Test]
    public async Task SelectNewestRollingTagBleedingEdgeSkipsDraftsAndNonMatching()
    {
        using var doc = JsonDocument.Parse(
            """
            [
              { "tag_name": "v1.0.0", "draft": false, "prerelease": false },
              { "tag_name": "stable-0.0.1+aaaa", "draft": false, "prerelease": false },
              { "tag_name": "bleeding-edge-0.0.1+deadbeef", "draft": true, "prerelease": true },
              { "tag_name": "bleeding-edge-0.0.1+cafebabe", "draft": false, "prerelease": true },
              { "tag_name": "bleeding-edge-0.0.1+older", "draft": false, "prerelease": true }
            ]
            """);

        var tag = BootstrapperReleaseClient.SelectNewestRollingTag(
            doc.RootElement,
            BootstrapperReleaseClient.BleedingEdgeRollingTagPrefix,
            requirePrerelease: true);
        await Assert.That(tag).IsEqualTo("bleeding-edge-0.0.1+cafebabe");
    }

    [Test]
    public async Task SelectNewestRollingTagReturnsNullWhenPrefixAbsentOnPage()
    {
        using var doc = JsonDocument.Parse(
            """
            [
              { "tag_name": "bleeding-edge-0.0.1+aaaa", "draft": false, "prerelease": true },
              { "tag_name": "bootstrap-v0.0.1", "draft": false, "prerelease": false }
            ]
            """);

        var tag = BootstrapperReleaseClient.SelectNewestRollingTag(
            doc.RootElement,
            BootstrapperReleaseClient.StableRollingTagPrefix,
            requirePrerelease: false);
        await Assert.That(tag).IsNull();
    }

    [Test]
    public async Task ResolveReleaseTagUsesMirrorFolderWhenOverrideSet()
    {
        var prior = Environment.GetEnvironmentVariable("INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable(
                "INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL",
                "http://127.0.0.1:8765/releases/download");
            using var client = BootstrapperReleaseClient.CreateDefault();
            await Assert.That(await client.ResolveReleaseTagAsync(
                    BootstrapperReleaseChannel.Stable, CancellationToken.None))
                .IsEqualTo("latest");
            await Assert.That(await client.ResolveReleaseTagAsync(
                    BootstrapperReleaseChannel.BleedingEdge, CancellationToken.None))
                .IsEqualTo("bleeding-edge");
            await Assert.That(await client.ResolveReleaseTagAsync(
                    BootstrapperReleaseChannel.ParseWire("bootstrap-v0.0.1"), CancellationToken.None))
                .IsEqualTo("bootstrap-v0.0.1");
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL", prior);
        }
    }

    [Test]
    public async Task StableTarballUrlUsesLatestReleaseTag()
    {
        Environment.SetEnvironmentVariable("INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL", null);
        using var client = BootstrapperReleaseClient.CreateDefault();
        var url = client.TarballUrl(BootstrapperReleaseChannel.Stable, "linux-x64");
        await Assert.That(url.ToString())
            .IsEqualTo("https://github.com/Azyyyyyy/Interfold/releases/download/latest/interfold-bootstrap-linux-x64.tar.gz");
    }

    [Test]
    public async Task BleedingEdgeTarballUrlUsesBleedingEdgeTag()
    {
        Environment.SetEnvironmentVariable("INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL", null);
        using var client = BootstrapperReleaseClient.CreateDefault();
        var url = client.TarballUrl(BootstrapperReleaseChannel.BleedingEdge, "linux-arm64");
        await Assert.That(url.ToString())
            .IsEqualTo("https://github.com/Azyyyyyy/Interfold/releases/download/bleeding-edge/interfold-bootstrap-linux-arm64.tar.gz");
    }

    [Test]
    public async Task ParseSha256SumFindsMatchingFile()
    {
        const string sums = """
            abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789  interfold-bootstrap-linux-x64.tar.gz
            1111111111111111111111111111111111111111111111111111111111111111  other.tar.gz
            """;

        var hash = BootstrapperReleaseClient.ParseSha256Sum(sums, "interfold-bootstrap-linux-x64.tar.gz");

        await Assert.That(hash).IsEqualTo("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");
    }

    [Test]
    public async Task ReleaseBaseUrlOverrideRewritesDownloadPaths()
    {
        var prior = Environment.GetEnvironmentVariable("INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable(
                "INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL",
                "http://127.0.0.1:8765/releases/download");
            using var client = BootstrapperReleaseClient.CreateDefault();
            await Assert.That(client.ReleaseAssetUrl(BootstrapperReleaseChannel.Stable, "linux-x64").ToString())
                .IsEqualTo("http://127.0.0.1:8765/releases/download/latest/interfold-bootstrap-linux-x64.tar.gz");
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL", prior);
        }
    }

    [Test]
    public async Task StableZipUrlUsesLatestReleaseTag()
    {
        Environment.SetEnvironmentVariable("INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL", null);
        using var client = BootstrapperReleaseClient.CreateDefault();
        var url = client.ReleaseAssetUrl(BootstrapperReleaseChannel.Stable, "win-x64");
        await Assert.That(url.ToString())
            .IsEqualTo("https://github.com/Azyyyyyy/Interfold/releases/download/latest/interfold-bootstrap-win-x64.zip");
    }

    [Test]
    public async Task ExtractBootstrapperFromZipWritesBinary()
    {
        using var scratch = TestSupport.NewScratchDir("bootstrapper-zip");
        var zipPath = Path.Combine(scratch.Path, "bundle.zip");
        var binaryName = HostPaths.BootstrapperFileName;
        var staged = Path.Combine(scratch.Path, binaryName);
        var extracted = Path.Combine(scratch.Path, "out", binaryName);

        await File.WriteAllTextAsync(staged, "zip-payload");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(binaryName);
            await using var entryStream = entry.Open();
            await using var fileStream = File.OpenRead(staged);
            await fileStream.CopyToAsync(entryStream);
        }

        BootstrapperReleaseClient.ExtractBootstrapperFromZip(zipPath, extracted);
        await Assert.That(File.Exists(extracted)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(extracted)).IsEqualTo("zip-payload");
    }

    [Test]
    public async Task ExtractBootstrapperFromTarballWritesBinary()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Host `tar` packaging is the Linux release path; zip coverage is above.
            return;
        }

        using var scratch = TestSupport.NewScratchDir("bootstrapper-tar");
        var tarball = Path.Combine(scratch.Path, "bundle.tar.gz");
        var binary = Path.Combine(scratch.Path, "interfold-bootstrap");

        await File.WriteAllTextAsync(binary, "#!/bin/sh\necho test\n");
        File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var create = await ProcessRunner.RunAsync(
            "tar",
            ["-czf", tarball, "-C", scratch.Path, "interfold-bootstrap"]);
        await Assert.That(create.ExitCode).IsEqualTo(0).Because(create.StdErr);

        File.Delete(binary);
        BootstrapperReleaseClient.ExtractBootstrapperFromTarball(tarball, binary);
        await Assert.That(File.Exists(binary)).IsTrue();
        var content = await File.ReadAllTextAsync(binary);
        await Assert.That(content).Contains("echo test");
    }
}
