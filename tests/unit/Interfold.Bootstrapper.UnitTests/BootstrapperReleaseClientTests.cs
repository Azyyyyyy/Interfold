using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

[NotInParallel("bootstrapper-release-env")]
public sealed class BootstrapperReleaseClientTests
{
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
            await Assert.That(client.VersionUrl(BootstrapperReleaseChannel.Stable).ToString())
                .IsEqualTo("http://127.0.0.1:8765/releases/download/latest/version.txt");
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTERFOLD_BOOTSTRAP_RELEASE_BASE_URL", prior);
        }
    }

    [Test]
    public async Task ExtractBootstrapperFromTarballWritesBinary()
    {
        using var scratch = TestSupport.NewScratchDir("bootstrapper-tar");
        var tarball = Path.Combine(scratch.Path, "bundle.tar.gz");
        var binary = Path.Combine(scratch.Path, "interfold-bootstrap");

        await File.WriteAllTextAsync(binary, "#!/bin/sh\necho test\n");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

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
