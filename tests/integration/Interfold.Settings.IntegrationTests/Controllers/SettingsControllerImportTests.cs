using System.Net;
using System.Text.Json;
using Interfold.IntegrationTests.Shared;
using Interfold.IntegrationTests.Shared.TestServices;
using Interfold.Settings.Contracts.Events;
using Interfold.Settings.Contracts.Ids;
using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Settings.Contracts.Models.Read;
using Interfold.Settings.Contracts.Models.Wire;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Settings.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public sealed class SettingsControllerImportTests(InMemoryWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task ImportOctocon_FreshDispatch_Returns202WithOperationId()
    {
        using var client = TestClient.NoRedirect(fixture);
        var principal = TestIds.NewSystemId("octocon-imp-202", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        var payload = JsonDocument.Parse("{\"alters\":[{\"id\":1,\"name\":\"AlterOne\"}]}").RootElement;

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/import?format=octocon",
            payload,
            principal);

        var envelope = await res.ReadEnvelopeAsync<ImportDispatchResponse>(HttpStatusCode.Accepted);

        using (Assert.Multiple())
        {
            await Assert.That(envelope.Data.OperationId.Value).IsNotEqualTo(Guid.Empty)
                .Because("Response must contain a non-empty OperationId.");
            await Assert.That(envelope.Data.Status).IsEqualTo(ImportOperationDispatchStatus.Queued)
                .Because("Fresh dispatch must surface as 'queued'.");
        }
    }

    [Test]
    public async Task ImportOctocon_CaseInsensitiveFormat_Returns202()
    {
        using var client = TestClient.NoRedirect(fixture);
        var principal = TestIds.NewSystemId("octocon-case-202", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        var payload = JsonDocument.Parse("{\"alters\":[{\"id\":1,\"name\":\"AlterOne\"}]}").RootElement;

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/import?format=OCTOCON",
            payload,
            principal);

        var envelope = await res.ReadEnvelopeAsync<ImportDispatchResponse>(HttpStatusCode.Accepted);

        using (Assert.Multiple())
        {
            await Assert.That(envelope.Data.OperationId.Value).IsNotEqualTo(Guid.Empty)
                .Because("Response must contain a non-empty OperationId.");
            await Assert.That(envelope.Data.Status).IsEqualTo(ImportOperationDispatchStatus.Queued)
                .Because("Fresh dispatch must surface as 'queued'.");
        }
    }

    [Test]
    public async Task ImportOctocon_AfterDispatch_WorkerProcessesAndPublishesCompletedEvent(CancellationToken token)
    {
        using var client = TestClient.NoRedirect(fixture);
        var principal = TestIds.NewSystemId("octocon-imp-wf", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        using var subscribeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        subscribeCts.CancelAfter(TimeSpan.FromSeconds(15));
        await using var enumerator = fixture.Factory.EventBus
            .SubscribeAsync<OctoconImportCompletedEvent>(subscribeCts.Token)
            .GetAsyncEnumerator(subscribeCts.Token);

        var payload = JsonDocument.Parse("{\"alters\":[{\"id\":1,\"name\":\"ImportedAlter\"}]}").RootElement;

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/import?format=octocon",
            payload,
            principal,
            ct: token);

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Accepted);

        OctoconImportCompletedEvent? observed = null;
        var principalSystemId = new SystemId(principal);
        while (await enumerator.MoveNextAsync())
        {
            if (enumerator.Current.TargetSystemId.RepresentsSameUserAs(principalSystemId))
            {
                observed = enumerator.Current;
                break;
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(observed).IsNotNull()
                .Because("Worker must process the queued Octocon import and publish OctoconImportCompletedEvent.");
            await Assert.That(observed!.AlterCount).IsEqualTo(1)
                .Because("Exactly 1 alter was present in the imported payload.");
        }
    }

    [Test]
    public async Task ImportPk_ThroughGenericEndpoint_Returns202WithOperationId()
    {
        using var client = TestClient.NoRedirect(fixture);
        var principal = TestIds.NewSystemId("pk-gen-202", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        var payload = JsonDocument.Parse("{\"token\":\"synthetic-pk-token\"}").RootElement;

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/import?format=pk",
            payload,
            principal);

        var envelope = await res.ReadEnvelopeAsync<ImportDispatchResponse>(HttpStatusCode.Accepted);

        using (Assert.Multiple())
        {
            await Assert.That(envelope.Data.OperationId.Value).IsNotEqualTo(Guid.Empty);
            await Assert.That(envelope.Data.Status).IsEqualTo(ImportOperationDispatchStatus.Queued);
        }
    }

    [Test]
    public async Task ImportPk_ThroughGenericEndpoint_CaseInsensitiveFormat_Returns202()
    {
        using var client = TestClient.NoRedirect(fixture);
        var principal = TestIds.NewSystemId("pk-case-202", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        var payload = JsonDocument.Parse("{\"token\":\"synthetic-pk-token\"}").RootElement;

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/import?format=PK",
            payload,
            principal);

        var envelope = await res.ReadEnvelopeAsync<ImportDispatchResponse>(HttpStatusCode.Accepted);

        using (Assert.Multiple())
        {
            await Assert.That(envelope.Data.OperationId.Value).IsNotEqualTo(Guid.Empty);
            await Assert.That(envelope.Data.Status).IsEqualTo(ImportOperationDispatchStatus.Queued);
        }
    }

    [Test]
    public async Task ImportSp_ThroughGenericEndpoint_Returns202WithOperationId()
    {
        using var client = TestClient.NoRedirect(fixture);
        var principal = TestIds.NewSystemId("sp-gen-202", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        var payload = JsonDocument.Parse("{\"token\":\"synthetic-sp-token\"}").RootElement;

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/import?format=sp",
            payload,
            principal);

        var envelope = await res.ReadEnvelopeAsync<ImportDispatchResponse>(HttpStatusCode.Accepted);

        using (Assert.Multiple())
        {
            await Assert.That(envelope.Data.OperationId.Value).IsNotEqualTo(Guid.Empty);
            await Assert.That(envelope.Data.Status).IsEqualTo(ImportOperationDispatchStatus.Queued);
        }
    }

    [Test]
    public async Task ImportSp_ThroughGenericEndpoint_CaseInsensitiveFormat_Returns202()
    {
        using var client = TestClient.NoRedirect(fixture);
        var principal = TestIds.NewSystemId("sp-case-202", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        var payload = JsonDocument.Parse("{\"token\":\"synthetic-sp-token\"}").RootElement;

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/import?format=SP",
            payload,
            principal);

        var envelope = await res.ReadEnvelopeAsync<ImportDispatchResponse>(HttpStatusCode.Accepted);

        using (Assert.Multiple())
        {
            await Assert.That(envelope.Data.OperationId.Value).IsNotEqualTo(Guid.Empty);
            await Assert.That(envelope.Data.Status).IsEqualTo(ImportOperationDispatchStatus.Queued);
        }
    }

    [Test]
    public async Task Import_InvalidJsonPayload_ReturnsBadRequest()
    {
        using var client = TestClient.NoRedirect(fixture);
        var principal = TestIds.NewSystemId("imp-bad-json", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        var payload = JsonDocument.Parse("[\"not\", \"an\", \"object\"]").RootElement;

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/import?format=pk",
            payload,
            principal);

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Import_EmptyToken_ReturnsBadRequest()
    {
        using var client = TestClient.NoRedirect(fixture);
        var principal = TestIds.NewSystemId("imp-empty-tok", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        var payload = JsonDocument.Parse("{\"token\":\"\"}").RootElement;

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/import?format=sp",
            payload,
            principal);

        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsNotEqualTo(HttpStatusCode.Accepted);
            await Assert.That((int)res.StatusCode).IsGreaterThanOrEqualTo(400);
        }
    }

    [Test]
    public async Task Import_MissingFormat_ReturnsBadRequest()
    {
        using var client = TestClient.NoRedirect(fixture);
        var principal = TestIds.NewSystemId("octocon-no-fmt", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        var payload = JsonDocument.Parse("{\"alters\":[]}").RootElement;

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/import",
            payload,
            principal);

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        var body = await res.Content.ReadAsStringAsync();
        await Assert.That(body).Contains(ErrorCodes.InvalidImportFormat.Value);
    }

    [Test]
    public async Task Import_InvalidFormat_ReturnsBadRequest()
    {
        using var client = TestClient.NoRedirect(fixture);
        var principal = TestIds.NewSystemId("octocon-bad-fmt", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        var payload = JsonDocument.Parse("{\"alters\":[]}").RootElement;

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/import?format=unknown_format",
            payload,
            principal);

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task ImportFormats_ReturnsSupportedFormats()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = TestIds.NewSystemId("imp-formats-user", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        using var res = await client.SendAuthedGetAsync("/api/settings/import/formats", principal);
        var envelope = await res.ReadEnvelopeAsync<string[]>(HttpStatusCode.OK);

        using (Assert.Multiple())
        {
            await Assert.That(envelope.Data).Contains("sp");
            await Assert.That(envelope.Data).Contains("pk");
            await Assert.That(envelope.Data).Contains("octocon");
        }
    }

    [Test]
    public async Task ImportFormats_Unauthenticated_ReturnsUnauthorized()
    {
        using var client = TestClient.NoRedirect(fixture);

        using var res = await client.GetAsync("/api/settings/import/formats");
        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }
}
