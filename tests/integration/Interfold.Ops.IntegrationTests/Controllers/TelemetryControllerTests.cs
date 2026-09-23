using System.Net;
using System.Text.Json;
using Interfold.IntegrationTests.Shared;
using Interfold.IntegrationTests.Shared.TestServices;
using Interfold.Ops.Api.Controllers;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Ops.IntegrationTests.Controllers;

public class TelemetryControllerTests : BaseEndpointTest
{
    [Test]
    public async Task GetOtlp_WhenUnset_Returns404()
    {
        await using var factory = new InterfoldWebApplicationFactory(PersistenceMode.InMemory);
        using var client = factory.CreateClient();
        var principal = TestIds.NewSystemId("sys-otel-unset", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        using var res = await client.SendAuthedGetAsync("/api/telemetry/otlp", principal);

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task GetOtlp_ServerEndpointAlone_DoesNotAdvertise()
    {
        await using var factory = new InterfoldWebApplicationFactory(PersistenceMode.InMemory)
            .WithConfiguration("OCTOCON_OTLP_ENDPOINT", "http://otel-collector:4317");
        using var client = factory.CreateClient();
        var principal = TestIds.NewSystemId("sys-otel-noadv", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        using var res = await client.SendAuthedGetAsync("/api/telemetry/otlp", principal);

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.NotFound)
            .Because("Advertising the server OTLP URL requires OCTOCON_ADVERTISE_OTLP_TO_CLIENTS=true");
    }

    [Test]
    public async Task GetOtlp_WhenAdvertiseOptIn_ReturnsServerEndpoint()
    {
        const string collector = "http://otel-collector:4317";
        await using var factory = new InterfoldWebApplicationFactory(PersistenceMode.InMemory)
            .WithConfiguration("OCTOCON_OTLP_ENDPOINT", collector)
            .WithConfiguration("OCTOCON_ADVERTISE_OTLP_TO_CLIENTS", "true");
        using var client = factory.CreateClient();
        var principal = TestIds.NewSystemId("sys-otel-adv", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        using var res = await client.SendAuthedGetAsync("/api/telemetry/otlp", principal);

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var dto = JsonSerializer.Deserialize<OtlpDiscoveryResponse>(await res.Content.ReadAsStringAsync());
        await Assert.That(dto!.OtlpHttpEndpoint).IsEqualTo(collector);
    }

    [Test]
    public async Task GetOtlp_ClientOverride_WinsOverAdvertise()
    {
        const string clientUrl = "http://public-collector:4318";
        await using var factory = new InterfoldWebApplicationFactory(PersistenceMode.InMemory)
            .WithConfiguration("OCTOCON_OTLP_ENDPOINT", "http://internal:4317")
            .WithConfiguration("OCTOCON_ADVERTISE_OTLP_TO_CLIENTS", "true")
            .WithConfiguration("OCTOCON_CLIENT_OTLP_HTTP_ENDPOINT", clientUrl);
        using var client = factory.CreateClient();
        var principal = TestIds.NewSystemId("sys-otel-ovr", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        using var res = await client.SendAuthedGetAsync("/api/telemetry/otlp", principal);

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"otlpHttpEndpoint\"");
        await Assert.That(body).DoesNotContain("\"data\"");

        var dto = JsonSerializer.Deserialize<OtlpDiscoveryResponse>(body);
        await Assert.That(dto!.OtlpHttpEndpoint).IsEqualTo(clientUrl);
    }

    [Test]
    public async Task GetOtlp_Unauthenticated_ReturnsEndpoint()
    {
        await using var factory = new InterfoldWebApplicationFactory(PersistenceMode.InMemory)
            .WithConfiguration("OCTOCON_CLIENT_OTLP_HTTP_ENDPOINT", "http://otel-collector:4318");
        using var client = factory.CreateClient();

        using var res = await client.GetAsync("/api/telemetry/otlp");

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var dto = JsonSerializer.Deserialize<OtlpDiscoveryResponse>(await res.Content.ReadAsStringAsync());
        await Assert.That(dto!.OtlpHttpEndpoint).IsEqualTo("http://otel-collector:4318");
    }

    [Test]
    public async Task ResolveDiscoveryEndpoint_RespectsPrecedence()
    {
        await Assert.That(TelemetryController.ResolveDiscoveryEndpoint(new ObservabilityConfiguration
        {
            OtlpEndpoint = "http://server",
            AdvertiseOtlpToClients = false,
        })).IsNull();

        await Assert.That(TelemetryController.ResolveDiscoveryEndpoint(new ObservabilityConfiguration
        {
            OtlpEndpoint = "http://server",
            AdvertiseOtlpToClients = true,
        })).IsEqualTo("http://server");

        await Assert.That(TelemetryController.ResolveDiscoveryEndpoint(new ObservabilityConfiguration
        {
            OtlpEndpoint = "http://server",
            AdvertiseOtlpToClients = true,
            ClientOtlpHttpEndpoint = "http://client",
        })).IsEqualTo("http://client");
    }
}
