using System.Net;
using Interfold.IntegrationTests.Shared;
using Interfold.IntegrationTests.Shared.TestServices;
using Interfold.Shared.Contracts;

namespace Interfold.Ops.IntegrationTests.Controllers;

/// <summary>
/// OAuth scheme registration is committed during the host build pipeline, not via
/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/>. This test owns its
/// own factory so it can pin <c>OCTOCON_GOOGLE_OAUTH_CLIENT_ID</c> without affecting
/// the shared <see cref="SharedDbFixture"/> session.
/// </summary>
public sealed class OAuthRegistrationTests : BaseEndpointTest
{
    [Test]
    public async Task Api_AuthRequest_IssuesChallengeRedirect_WhenChallengeEnabledAndSchemeConfigured()
    {
        // The Google challenge scheme + endpoint are baked into the API as constants — see
        // OAuthChallengeServiceCollectionExtensions. The scheme is only registered when
        // OCTOCON_GOOGLE_OAUTH_CLIENT_ID is set (the operator's "I want to use Google"
        // signal); the test pins it here so the scheme registers and the controller issues
        // a real challenge. Because scheme registration is a one-shot startup step rather
        // than IOptionsMonitor-bound, this test owns its own factory.
        using var factory = new InterfoldWebApplicationFactory(PersistenceMode.InMemory)
            .WithConfiguration("OCTOCON_GOOGLE_OAUTH_CLIENT_ID", "test-client-id");

        using var client = TestClient.NoRedirect(factory);

        using var response = await client.GetAsync("/auth/google");

        var locationHeader = response.Headers.Location;
        var location = locationHeader!.ToString();
        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found).IsTrue();
            await Assert.That(locationHeader).IsNotNull();
            await Assert.That(location.StartsWith("https://accounts.google.com/o/oauth2/v2/auth", StringComparison.Ordinal)).IsTrue();
            await Assert.That(location.Contains("%2Fauth%2Fgoogle%2Fcallback", StringComparison.Ordinal)).IsTrue();
        }
    }
}
