using System.Net;
using Interfold.Contracts;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Auth;
using Interfold.IntegrationTests.TestServices;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;

namespace Interfold.IntegrationTests.Controllers;

[Category("Auth")]
[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class AuthLinkControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    private static string UniqueId(string prefix) => TestIds.NewSystemId(prefix, maxLen: 24);

    [Test]
    public async Task Begin_WithoutRedirectUri_ReturnsBadRequest()
    {
        using var client = TestClient.NoRedirect(fixture);

        using var res = await client.GetAsync("/auth/link/discord");
        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        var error = await res.ReadErrorAsync(HttpStatusCode.BadRequest);
        await Assert.That(error.Code).IsEqualTo(ErrorCodes.MissingRedirectUri);
    }

    [Test]
    public async Task Callback_FullFlow_Succeeds()
    {
        using var client = TestClient.NoRedirect(fixture);
        var user = UniqueId("link-flow-u");
        await EnsureUserExistsAsync(client, user);

        // 1. Get Link Token for the user
        using var tokenRes = await client.SendAuthedGetAsync("/api/settings/link_token", user);
        var tokenEnv = await tokenRes.ReadEnvelopeAsync<LinkTokenReadModel>(HttpStatusCode.OK);
        var token = tokenEnv.Data.Token;
        await Assert.That(token.Value).IsNotEmpty();

        // 2. Perform Link Callback
        var discordIdStr = $"discord-link-{Guid.NewGuid():N}";
        var clientRedirectUri = "https://test.invalid/link/done";

        var callbackRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/auth/link/discord/callback?uid={Uri.EscapeDataString(discordIdStr)}");
        callbackRequest.Headers.Add("Cookie",
            $"octocon_link_token={Uri.EscapeDataString(token.Value)}; octocon_link_redirect_uri={Uri.EscapeDataString(clientRedirectUri)}");

        using var response = await client.SendAsync(callbackRequest);
        await Assert.That(response.StatusCode)
            .Satisfies(x => x is HttpStatusCode.Redirect or HttpStatusCode.Found
                            or HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect
                            or HttpStatusCode.PermanentRedirect)
            .Because($"Expected redirect to client redirect URI, got {(int)response.StatusCode}");

        var location = response.Headers.Location?.ToString();
        await Assert.That(location).IsEqualTo(clientRedirectUri);

        // 3. Verify in repository that identity is linked
        var accountRepo = fixture.Factory.Services.GetRequiredService<IAccountRepository>();
        var linkedSystemId = await accountRepo.TryFindSystemIdByDiscordIdAsync(new DiscordId(discordIdStr));
        await Assert.That(linkedSystemId).IsNotNull();
        await Assert.That(linkedSystemId!.Value.Value).IsEqualTo(user);
    }

    [Test]
    public async Task Callback_WhenAlreadyLinkedToAnotherUser_Returns500InternalServerError()
    {
        using var client = TestClient.NoRedirect(fixture);
        var userA = UniqueId("link-err-a");
        var userB = UniqueId("link-err-b");
        await EnsureUserExistsAsync(client, userA);
        await EnsureUserExistsAsync(client, userB);

        var accountRepo = fixture.Factory.Services.GetRequiredService<IAccountRepository>();

        // Link user A to discord ID
        var discordIdStr = $"discord-err-{Guid.NewGuid():N}";
        var linkResult = await accountRepo.LinkIdentityToUserAsync(
            ScopedSystemId.Compose(ScyllaKeyspace.Nam, userA).AsSystemId(),
            ProviderIdentity.FromDiscord(new DiscordId(discordIdStr)));
        await Assert.That(linkResult).IsEqualTo(AccountLinkResult.Success);

        // Get link token for user B
        using var tokenRes = await client.SendAuthedGetAsync("/api/settings/link_token", userB);
        var tokenEnv = await tokenRes.ReadEnvelopeAsync<LinkTokenReadModel>(HttpStatusCode.OK);
        var token = tokenEnv.Data.Token;

        // Try linking discord ID to user B
        var clientRedirectUri = "https://test.invalid/link/done";
        var callbackRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/auth/link/discord/callback?uid={Uri.EscapeDataString(discordIdStr)}");
        callbackRequest.Headers.Add("Cookie",
            $"octocon_link_token={Uri.EscapeDataString(token.Value)}; octocon_link_redirect_uri={Uri.EscapeDataString(clientRedirectUri)}");

        using var response = await client.SendAsync(callbackRequest);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
    }
}