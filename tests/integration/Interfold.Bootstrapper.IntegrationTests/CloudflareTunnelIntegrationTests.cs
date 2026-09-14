using System.Text;
using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Publish with Cloudflare Tunnel enabled against a local mock of the Cloudflare REST API.
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
[Explicit]
public sealed class CloudflareTunnelIntegrationTests(UbuntuDinDFixture dinD)
{
    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task PublishWithTunnelWritesTokenAndEmitsPrivateCloudflaredCompose()
    {
        var scratch = await dinD.CreateScratchAsync(
            nameof(PublishWithTunnelWritesTokenAndEmitsPrivateCloudflaredCompose),
            TestConfigPaths.CloudflareTunnelConfig);

        var port = 19878;
        var setup = await CloudflareApiMock.StartAsync(
            dinD,
            CloudflareApiMock.TunnelScriptPath,
            containerDir: "/tmp/cf-api-mock",
            port,
            logPath: "/tmp/cf-api-mock.log",
            pidPath: "/tmp/cf-api-mock.pid");
        await Assert.That(setup.ExitCode).IsEqualTo(0L).Because(setup.Stderr + setup.Stdout);

        try
        {
            var publish = await dinD.ExecAsync([
                "sh", "-c",
                $"INTERFOLD_CLOUDFLARE_API_BASE_URL=http://127.0.0.1:{port}/client/v4/ " +
                $"{DinDFixtureBase.BootstrapperMountPath}/interfold-bootstrap publish " +
                $"--config {scratch.ConfigPath} --output-dir {scratch.OutputDir} --non-interactive",
            ]);
            await Assert.That(publish.ExitCode).IsEqualTo(0L).Because(publish.Stderr + publish.Stdout);

            var token = await dinD.CopyOutAsTextAsync($"{scratch.OutputDir}/secrets/cloudflare-tunnel.token");
            await Assert.That(token.Trim()).IsEqualTo("connector-token-jwt");

            var state = await dinD.CopyOutAsTextAsync($"{scratch.OutputDir}/.cloudflare-tunnel.json");
            await Assert.That(state).Contains("tun-test");
            await Assert.That(state).Contains("acct-test");
            await Assert.That(state).Contains("zone-test");

            var compose = Encoding.UTF8.GetString(await dinD.CopyOutAsync($"{scratch.OutputDir}/docker-compose.yaml"));
            await Assert.That(compose).Contains("cloudflared");
            await Assert.That(compose).Contains("cloudflare/cloudflared");
            await Assert.That(compose).Contains("--token-file");
            await Assert.That(compose).DoesNotContain($"\"{scratch.Ports.EdgeHttp}:80\"")
                .Because("tunnel mode must not publish edge host ports");
            await Assert.That(compose).DoesNotContain($"\"{scratch.Ports.EdgeHttps}:443\"");

            var createBody = await dinD.CopyOutAsTextAsync("/tmp/cf-tunnel-create.json");
            await Assert.That(createBody).Contains("interfold-test");
            await Assert.That(createBody).Contains("cloudflare");
        }
        finally
        {
            await CloudflareApiMock.StopAsync(dinD, "/tmp/cf-api-mock.pid");
        }
    }
}
