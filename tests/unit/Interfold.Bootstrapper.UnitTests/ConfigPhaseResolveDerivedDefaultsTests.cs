using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Unit tests for <see cref="ConfigPhase.ResolveDerivedDefaults"/>.
/// Always-on edge: HTTPS modes use edge.ports.https; plaintext uses edge.ports.http.
/// </summary>
public sealed class ConfigPhaseResolveDerivedDefaultsTests
{
    [Test]
    public async Task EdgePathUsesEdgeHttpsForApiAndCors()
    {
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(hosts: "api.example.com");
        cfg.Edge.Ports.Https = 443;

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://api.example.com");
        await Assert.That(cfg.Api.OAuth.JwtAuthority).IsEqualTo("https://api.example.com");
        await Assert.That(cfg.Api.CorsAllowedOrigins).Contains("https://api.example.com");
    }

    [Test]
    public async Task EdgePathNonDefaultEdgeHttpsKeepsPortSuffix()
    {
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(hosts: "api.example.com");
        cfg.Edge.Ports.Https = 8443;

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://api.example.com:8443");
        await Assert.That(cfg.Api.CorsAllowedOrigins).Contains("https://api.example.com:8443");
    }

    [Test]
    public async Task PlaintextEdgeUsesEdgeHttp()
    {
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(
            edgeTlsMode: EdgeTlsMode.None,
            hosts: "api.example.com");
        cfg.Edge.Ports.Http = 8080;

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.Api.OAuth.CallbackBaseUrl).IsEqualTo("http://api.example.com:8080");
        await Assert.That(cfg.Api.CorsAllowedOrigins).Contains("http://api.example.com:8080");
    }

    [Test]
    public async Task PlaintextEdgeHttpPort80OmitsPortSuffix()
    {
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(
            edgeTlsMode: EdgeTlsMode.None,
            hosts: "api.example.com");
        cfg.Edge.Ports.Http = 80;

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.Api.OAuth.CallbackBaseUrl).IsEqualTo("http://api.example.com");
        await Assert.That(cfg.Api.CorsAllowedOrigins).Contains("http://api.example.com");
    }

    [Test]
    public async Task EdgeSubdomainSplitsApiAndCorsHosts()
    {
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(
            edgeRouting: EdgeRoutingMode.Subdomain,
            hosts: "unused.example.com");
        cfg.Edge.Routing.ApiHost = "api.example.com";
        cfg.Edge.Routing.WebHost = "app.example.com";
        cfg.Edge.Ports.Https = 443;

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://api.example.com");
        await Assert.That(cfg.Api.OAuth.JwtAuthority).IsEqualTo("https://api.example.com");
        await Assert.That(cfg.Api.CorsAllowedOrigins).Contains("https://app.example.com");
        await Assert.That(cfg.Api.CorsAllowedOrigins).DoesNotContain("https://api.example.com");
    }

    [Test]
    public async Task MultipleHostsProduceMultipleCorsEntries()
    {
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(
            hosts: ["a.example.com", "b.example.com"]);

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://a.example.com");
        await Assert.That(cfg.Api.CorsAllowedOrigins).Contains("https://a.example.com");
        await Assert.That(cfg.Api.CorsAllowedOrigins).Contains("https://b.example.com");
    }

    [Test]
    public async Task NonEmptyCallbackWinsOverDerived()
    {
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(hosts: "api.example.com");
        cfg.Api.OAuth.CallbackBaseUrl = "https://custom.example.com";

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://custom.example.com");
    }

    [Test]
    public async Task NonEmptyJwtWinsOverDerived()
    {
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(hosts: "api.example.com");
        cfg.Api.OAuth.JwtAuthority = "https://issuer.example.com";

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.Api.OAuth.JwtAuthority).IsEqualTo("https://issuer.example.com");
    }

    [Test]
    public async Task NonEmptyCorsWinsOverDerived()
    {
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(hosts: "api.example.com");
        cfg.Api.CorsAllowedOrigins = ["https://spa.example.com"];

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.Api.CorsAllowedOrigins).Contains("https://spa.example.com");
        await Assert.That(cfg.Api.CorsAllowedOrigins.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EmptyHostsLeavesApiRuntimeUntouched()
    {
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime();
        cfg.Edge.Hosts = [];

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.Api.OAuth.CallbackBaseUrl).IsEqualTo(string.Empty);
        await Assert.That(cfg.Api.CorsAllowedOrigins).IsEmpty();
    }

    [Test]
    public async Task Ipv6PrimaryIsBracketWrapped()
    {
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(
            edgeTlsMode: EdgeTlsMode.None,
            hosts: "::1");

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.Api.OAuth.CallbackBaseUrl).IsEqualTo("http://[::1]");
        await Assert.That(cfg.Api.CorsAllowedOrigins).Contains("http://[::1]");
    }

    [Test]
    public async Task CidrEntriesSkippedForCorsPrimaryIsFirstLeaf()
    {
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(
            edgeTlsMode: EdgeTlsMode.None,
            hosts: ["192.168.1.0/24", "192.168.1.42"]);

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.Api.OAuth.CallbackBaseUrl).IsEqualTo("http://192.168.1.42");
        await Assert.That(cfg.Api.CorsAllowedOrigins).Contains("http://192.168.1.42");
        await Assert.That(cfg.Api.CorsAllowedOrigins.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CloudflareTunnelUsesBareHttpsWithoutPort()
    {
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(
            edgeTlsMode: EdgeTlsMode.PrivateCa,
            hosts: "api.example.com");
        cfg.Edge.Ports.Https = 8443;
        cfg.Edge.Cloudflare.Enabled = true;

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.Api.OAuth.CallbackBaseUrl).IsEqualTo("https://api.example.com");
        await Assert.That(cfg.Api.OAuth.JwtAuthority).IsEqualTo("https://api.example.com");
        await Assert.That(cfg.Api.CorsAllowedOrigins).Contains("https://api.example.com");
    }
}
