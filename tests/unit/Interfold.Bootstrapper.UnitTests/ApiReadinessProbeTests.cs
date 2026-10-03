using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class ApiReadinessProbeTests
{
    [Test]
    public async Task PublicHttpsUsesPrimaryHostAndCustomPort()
    {
        var config = new BootstrapConfig();
        config.Edge.Hosts = ["api.example.com"];
        config.Edge.TlsMode = EdgeTlsMode.PrivateCa;
        config.Edge.Ports.Https = 8443;

        await Assert.That(ApiReadinessProbe.ResolveReadyUrl(config))
            .IsEqualTo("https://api.example.com:8443/health/ready");
    }

    [Test]
    public async Task PublicHttpOmitsDefaultPort()
    {
        var config = new BootstrapConfig();
        config.Edge.Hosts = ["api.example.com"];
        config.Edge.TlsMode = EdgeTlsMode.None;

        await Assert.That(ApiReadinessProbe.ResolveReadyUrl(config))
            .IsEqualTo("http://api.example.com/health/ready");
    }

    [Test]
    public async Task SubdomainUsesTheApiNamePeopleOpen()
    {
        var config = new BootstrapConfig();
        config.Edge.Hosts = ["AP-MAIN.local"];
        config.Edge.TlsMode = EdgeTlsMode.PrivateCa;
        config.Edge.Routing.Mode = EdgeRoutingMode.Subdomain;
        config.Edge.Routing.ApiHost = "api.AP-MAIN.local";
        config.Edge.Routing.WebHost = "web.AP-MAIN.local";

        await Assert.That(ApiReadinessProbe.ResolveReadyUrl(config))
            .IsEqualTo("https://api.AP-MAIN.local/health/ready");
    }

    [Test]
    public async Task UnresolvablePublicNameFailsWithoutWaitingOutTheBudget()
    {
        var config = new BootstrapConfig();
        config.Edge.Hosts = ["health-check.invalid"];
        config.Edge.TlsMode = EdgeTlsMode.PrivateCa;
        using var scratch = TestSupport.NewScratchDir("ready-probe");
        var logger = new PhaseLogger(TestSupport.MakeOptions(outputDir: scratch.Path));
        var started = DateTime.UtcNow;

        var err = await ApiReadinessProbe.TryWaitUntilAsync(
            config, started.AddMinutes(5), logger, CancellationToken.None);

        await Assert.That(err).IsNotNull();
        await Assert.That(err!).Contains("did not resolve");
        await Assert.That(DateTime.UtcNow - started).IsLessThan(TimeSpan.FromSeconds(40));
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
