using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class ApiReadinessProbeTests
{
    [Test]
    public async Task LocalhostHttpsWhenEdgePublishesPorts()
    {
        var config = new BootstrapConfig();
        config.Edge.Hosts = ["api.example.com"];
        config.Edge.TlsMode = EdgeTlsMode.PrivateCa;
        config.Edge.Ports.Https = 8443;

        await Assert.That(ApiReadinessProbe.ResolveReadyUrl(config))
            .IsEqualTo("https://localhost:8443/health/ready");
    }

    [Test]
    public async Task LocalhostHttpOmitsDefaultPort()
    {
        var config = new BootstrapConfig();
        config.Edge.Hosts = ["api.example.com"];
        config.Edge.TlsMode = EdgeTlsMode.None;

        await Assert.That(ApiReadinessProbe.ResolveReadyUrl(config))
            .IsEqualTo("http://localhost/health/ready");
    }

    [Test]
    public async Task CloudflareUsesPublicApiHostInsteadOfLocalhost()
    {
        var config = new BootstrapConfig();
        config.Edge.Cloudflare.Enabled = true;
        config.Edge.TlsMode = EdgeTlsMode.PrivateCa;
        config.Edge.Routing.Mode = EdgeRoutingMode.Subdomain;
        config.Edge.Routing.ApiHost = "api.example.com";
        config.Edge.Routing.WebHost = "web.example.com";

        await Assert.That(ApiReadinessProbe.ResolveReadyUrl(config))
            .IsEqualTo("https://api.example.com/health/ready");
    }

    [Test]
    public async Task AccessAllowlistFailsFastWithoutServiceToken()
    {
        var config = new BootstrapConfig();
        config.Edge.Cloudflare.Enabled = true;
        config.Edge.Cloudflare.Access.Enabled = true;
        config.Edge.Hosts = ["api.example.com"];
        using var scratch = TestSupport.NewScratchDir("access-probe");
        var logger = new PhaseLogger(TestSupport.MakeOptions(outputDir: scratch.Path));

        var err = await ApiReadinessProbe.TryWaitUntilAsync(
            config, DateTime.UtcNow.AddMinutes(1), logger, CancellationToken.None, scratch.Path);

        await Assert.That(err).IsNotNull();
        await Assert.That(err!).Contains("cloudflare-access-service.token");
    }
}
