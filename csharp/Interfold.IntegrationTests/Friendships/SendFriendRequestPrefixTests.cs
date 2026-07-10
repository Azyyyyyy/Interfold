using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Friendships;

/// <summary>
/// End-to-end pins on the <see cref="Interfold.Contracts.Ids.LookupHandle"/> dispatch
/// matrix, exercised through the <c>/api/friend-requests/{id}</c> route so the whole
/// stack (controller → command handler → friendship repo → registry) is in play.
///
/// <para>
/// The tests run under all three fixtures via TUnit's <c>[ClassDataSource]</c>
/// parameterisation. Cases split into two families:
/// </para>
///
/// <list type="bullet">
///   <item>
///     <description>
///       <b>Backend-uniform</b> — dispatch matrix contracts that must hold regardless of
///       which persistence backend is behind the API: bare id / <c>id:</c> / unknown-prefix
///       / <c>username:unknown</c> / <c>discord:unknown</c>. Every fixture goes through
///       these to lock in that InMemory / Scylla / Cassandra route identically.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Backend-specific</b> — <c>username:known</c> success requires the Scylla
///       <c>users_by_username</c> reverse index; InMemory has no such index so the same
///       route returns <c>NoUser</c>. That divergence is intentional (documented in the
///       InMemory friendship-repo comment) and pinned by the two <see cref="RecipientBackend"/>
///       branches below.
///     </description>
///   </item>
/// </list>
/// </summary>
[Category("Friendships")]
[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public sealed class SendFriendRequestPrefixTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    private enum RecipientBackend { InMemory, Regional }

    private RecipientBackend Backend => fixture is InMemoryWebFactoryFixture
        ? RecipientBackend.InMemory
        : RecipientBackend.Regional;

    // ---------------- Backend-uniform: bare id + id: prefix ----------------

    [Test]
    public async Task SendFriendRequest_BareRecipientId_Returns204()
    {
        using var client = fixture.Factory.CreateClient();
        var sender = UniqueId("s7-send-bare-a");
        var recipient = UniqueId("s7-send-bare-b");
        await EnsureUserExistsAsync(client, sender);
        await EnsureUserExistsAsync(client, recipient);

        var status = await SendFriendRequestAsync(client, sender, recipient);
        await Assert.That(status).IsEqualTo(HttpStatusCode.NoContent)
            .Because("Bare recipient id is the baseline shape — must resolve to the recipient and land a 204.");
    }

    [Test]
    public async Task SendFriendRequest_ExplicitIdPrefix_Returns204()
    {
        using var client = fixture.Factory.CreateClient();
        var sender = UniqueId("s7-send-id-a");
        var recipient = UniqueId("s7-send-id-b");
        await EnsureUserExistsAsync(client, sender);
        await EnsureUserExistsAsync(client, recipient);

        var status = await SendFriendRequestAsync(client, sender, $"id:{recipient}");
        await Assert.That(status).IsEqualTo(HttpStatusCode.NoContent)
            .Because("'id:{recipient}' must dispatch identically to the bare form — both are Kind.Id under LookupHandle and target user_registry.user_id.");
    }

    // ---------------- Backend-uniform: strict rejection --------------------

    [Test]
    public async Task SendFriendRequest_UnknownPrefix_ReturnsNoUser()
    {
        using var client = fixture.Factory.CreateClient();
        var sender = UniqueId("s7-send-unk-a");
        await EnsureUserExistsAsync(client, sender);

        // An unknown prefix must NOT get silently stripped and re-tried as a bare id
        // (which by coincidence would sometimes find a user). The whole input hits
        // user_registry, misses, and returns NoUser.
        var (status, entityRef) = await SendFriendRequestWithEntityRefAsync(
            client, sender, "xxx:definitely-not-a-real-user");

        using (Assert.Multiple())
        {
            await Assert.That(status).IsEqualTo(HttpStatusCode.UnprocessableEntity)
                .Because("Unknown non-region prefix must not silently strip — the whole input is looked up as a bare id, misses, and returns NoUser (422).");
            await Assert.That(entityRef).IsEqualTo("friend_request:no_user");
        }
    }

    [Test]
    public async Task SendFriendRequest_UsernamePrefix_UnknownUsername_ReturnsNoUser()
    {
        using var client = fixture.Factory.CreateClient();
        var sender = UniqueId("s7-send-unamemiss-a");
        await EnsureUserExistsAsync(client, sender);

        var (status, entityRef) = await SendFriendRequestWithEntityRefAsync(
            client, sender, "username:definitely-not-a-real-username-12345");

        using (Assert.Multiple())
        {
            await Assert.That(status).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(entityRef).IsEqualTo("friend_request:no_user")
                .Because("Username lookup that misses must NOT fall back to treating the literal 'username:...' as a system id.");
        }
    }

    [Test]
    public async Task SendFriendRequest_DiscordPrefix_UnknownDiscordId_ReturnsNoUser_NoPhantomCreate()
    {
        using var client = fixture.Factory.CreateClient();
        var sender = UniqueId("s7-send-discmiss-a");
        await EnsureUserExistsAsync(client, sender);

        // The unique Discord id makes this fresh per run. If the friendship dispatch
        // ever regressed to the auto-provisioning FindOrCreateSystemIdAsync (the sole
        // create-on-miss OAuth-login surface), the account would be created and this
        // test would flip to 204. NoUser is the load-bearing pin on TryFind delegation.
        var uniqueDiscord = $"99999{DateTime.UtcNow.Ticks}";
        var (status, entityRef) = await SendFriendRequestWithEntityRefAsync(
            client, sender, $"discord:{uniqueDiscord}");

        using (Assert.Multiple())
        {
            await Assert.That(status).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(entityRef).IsEqualTo("friend_request:no_user")
                .Because("Discord dispatch delegates to TryFindSystemIdByDiscordIdAsync (not FindOrCreate) precisely so an unknown Discord id can't spawn a phantom account through the friendship surface.");
        }
    }

    // ---------------- Backend-specific: username success -------------------

    [Test]
    public async Task SendFriendRequest_UsernamePrefix_KnownUsername_DispatchMatchesBackendContract()
    {
        using var client = fixture.Factory.CreateClient();
        var sender = UniqueId("s7-send-uname-a");
        var recipientPrincipal = UniqueId("s7-send-uname-b");
        // Deterministic per-run username so parallel test runs don't collide on the
        // users_by_username partition key.
        var recipientUsername = $"unameuser{DateTime.UtcNow.Ticks % 1_000_000}";
        await EnsureUserExistsAsync(client, sender);
        await EnsureUserExistsAsync(client, recipientPrincipal, recipientUsername);

        var (status, entityRef) = await SendFriendRequestWithEntityRefAsync(
            client, sender, $"username:{recipientUsername}");

        if (Backend == RecipientBackend.InMemory)
        {
            // InMemory has no users_by_username reverse index; the correct answer is
            // "no such user" rather than fabricating a SystemId('username:...').
            using (Assert.Multiple())
            {
                await Assert.That(status).IsEqualTo(HttpStatusCode.UnprocessableEntity);
                await Assert.That(entityRef).IsEqualTo("friend_request:no_user")
                    .Because("InMemory has no username reverse index — the Kind.Username branch returns null which surfaces as NoUser. This is documented in the InMemoryFriendshipRepository dispatch comment.");
            }
        }
        else
        {
            await Assert.That(status).IsEqualTo(HttpStatusCode.NoContent)
                .Because("Scylla/Cassandra populate users_by_username on /api/settings/username, so the username-prefixed friend request must resolve and land 204.");
        }
    }

    // ---------------- Helpers ----------------------------------------------

    private static string UniqueId(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    private static async Task<HttpStatusCode> SendFriendRequestAsync(
        HttpClient client, string sender, string recipientHandle)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/friend-requests/{recipientHandle}");
        req.Content = JsonContent.Create(new { });
        AttachPrincipalAuth(req, client, sender);
        var res = await client.SendAsync(req);
        return res.StatusCode;
    }

    private static async Task<(HttpStatusCode Status, string? EntityRef)> SendFriendRequestWithEntityRefAsync(
        HttpClient client, string sender, string recipientHandle)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/friend-requests/{recipientHandle}");
        req.Content = JsonContent.Create(new { });
        AttachPrincipalAuth(req, client, sender);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        if (res.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body))
        {
            return (res.StatusCode, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            // ErrorResponse serialises entityRef under any of: entity_ref / entityRef.
            // The exact casing depends on the JsonSerializerOptions the API is configured
            // with; scan case-insensitively so the test stays resilient to policy tweaks.
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.Equals("entity_ref", StringComparison.OrdinalIgnoreCase) ||
                    prop.Name.Equals("entityRef", StringComparison.OrdinalIgnoreCase))
                {
                    return (res.StatusCode, prop.Value.GetString());
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON body (some error paths return plain text); fall through and return null.
        }

        return (res.StatusCode, null);
    }
}
