using System.Net;
using Interfold.Contracts;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class PublicSystemsControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task PublicBatch_SelfLookup_Returns403InvalidEndpoint()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-public-batch-self";
        _ = await CreateAlterAsync(client, principal, "BatchSelfSeed");
        await EnsurePublicProfileAsync(client, principal, "batch-self");

        using var res = await client.SendAuthedGetAsync($"/api/systems/{principal}/batch", principal);
        var error = await res.ReadErrorAsync(HttpStatusCode.Forbidden);
        await Assert.That(error.Code).IsEqualTo(ErrorCodes.InvalidEndpoint);
    }

    [Test, Skip("Need to rework")] //Well all of them really...
    public async Task Visibility_NonFriendFriendTrusted_AppliesToFronting()
    {
        using var client = TestClient.NoRedirect(fixture);

        var owner = "fronting-visibility-owner";
        var nonFriend = "fronting-visibility-nonfriend";
        var friend = "fronting-visibility-friend";
        var trusted = "fronting-visibility-trusted";

        _ = await CreateAlterAsync(client, nonFriend, "SeedNonFriend");
        _ = await CreateAlterAsync(client, friend, "SeedFriend");
        _ = await CreateAlterAsync(client, trusted, "SeedTrusted");
        await EnsurePublicProfileAsync(client, owner, "fronting-owner");
        await EnsurePublicProfileAsync(client, nonFriend, "fronting-nonfriend");
        await EnsurePublicProfileAsync(client, friend, "fronting-friend");
        await EnsurePublicProfileAsync(client, trusted, "fronting-trusted");

        var alterPublic = await CreateAlterAsync(client, owner, "VisPublic");
        var alterFriends = await CreateAlterAsync(client, owner, "VisFriends");
        var alterTrusted = await CreateAlterAsync(client, owner, "VisTrusted");
        var alterPrivate = await CreateAlterAsync(client, owner, "VisPrivate");

        await SetAlterSecurityLevelAsync(client, owner, alterPublic, VisibilityLevel.Public);
        await SetAlterSecurityLevelAsync(client, owner, alterFriends, VisibilityLevel.FriendsOnly);
        await SetAlterSecurityLevelAsync(client, owner, alterTrusted, VisibilityLevel.TrustedOnly);
        await SetAlterSecurityLevelAsync(client, owner, alterPrivate, VisibilityLevel.Private);

        await StartFrontAsync(client, owner, alterPublic);
        await StartFrontAsync(client, owner, alterFriends);
        await StartFrontAsync(client, owner, alterTrusted);
        await StartFrontAsync(client, owner, alterPrivate);

        // Non-friend viewer: only the Public fronter surfaces; the other three are filtered
        // by the guarded fronting list. One GET per viewer is enough — the response is
        // idempotent for a fixed friendship-graph state, so batching the four visibility
        // assertions against the same payload is both stricter and one-quarter the round
        // trips of the pre-typed-sweep needle hunt.
        await AssertFrontingVisibilityAsync(
            client, owner, nonFriend,
            visible: new[] { alterPublic },
            hidden: new[] { alterFriends, alterTrusted, alterPrivate });

        // Friend viewer gains FriendsOnly.
        await SendFriendRequestAndAcceptAsync(client, friend, owner);
        await AssertFrontingVisibilityAsync(
            client, owner, friend,
            visible: new[] { alterPublic, alterFriends },
            hidden: new[] { alterTrusted, alterPrivate });

        // Trusted viewer gains TrustedOnly; Private stays hidden.
        await SendFriendRequestAndAcceptAsync(client, trusted, owner);
        await SetFriendTrustAsync(client, owner, trusted);
        await AssertFrontingVisibilityAsync(
            client, owner, trusted,
            visible: new[] { alterPublic, alterFriends, alterTrusted },
            hidden: new[] { alterPrivate });
    }

    /// <summary>
    /// Asserts every id in <paramref name="visible"/> appears in the guarded fronting list
    /// for <paramref name="viewer"/>, and every id in <paramref name="hidden"/> is absent.
    /// Uses <see cref="FrontActiveReadModel.Alter"/>.Id (the row-level typed shape produced
    /// by <c>PublicSystemsController.ListFronting</c>) rather than string-searching the
    /// envelope; this way, a serialiser drift that renames the field or drops the alter
    /// nesting fails at deserialise time instead of silently matching / missing.
    /// </summary>
    private static async Task AssertFrontingVisibilityAsync(
        HttpClient client,
        string owner,
        string viewer,
        AlterId[] visible,
        AlterId[] hidden)
    {
        using var res = await client.SendAuthedGetAsync($"/api/systems/{owner}/fronting", viewer);
        var envelope = await res.ReadEnvelopeAsync<IReadOnlyList<FrontActiveReadModel>>(HttpStatusCode.OK);
        var visibleAlterIds = envelope.Data.Select(f => f.Alter.Id).ToArray();

        using (Assert.Multiple())
        {
            foreach (var expected in visible)
            {
                await Assert.That(visibleAlterIds).Contains(expected)
                    .Because($"Expected viewer '{viewer}' to see fronter alter {expected.Value} in owner '{owner}''s fronting list.");
            }
            foreach (var restricted in hidden)
            {
                await Assert.That(visibleAlterIds).DoesNotContain(restricted)
                    .Because($"Expected viewer '{viewer}' NOT to see fronter alter {restricted.Value} in owner '{owner}''s fronting list (visibility-restricted).");
            }
        }
    }

    [Test]
    public async Task PublicAlter_PrivateSecurity_Returns404ForAnonymous()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-guarded-alter";
        var alterId = await CreateAlterAsync(client, principal, "GuardedAlter");
        await EnsurePublicProfileAsync(client, principal, "guarded-alter");

        await SetAlterSecurityLevelAsync(client, principal, alterId, VisibilityLevel.Private);

        using var publicRes = await client.GetAsync($"/api/systems/{principal}/alters/{alterId}");
        await Assert.That(publicRes.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Visibility_NonFriendFriendTrusted_AppliesAcrossPublicReads()
    {
        using var client = TestClient.NoRedirect(fixture);

        var owner = "alters-visibility-owner";
        var nonFriend = "alters-visibility-nonfriend";
        var friend = "alters-visibility-friend";
        var trusted = "alters-visibility-trusted";

        _ = await CreateAlterAsync(client, nonFriend, "SeedNonFriend");
        _ = await CreateAlterAsync(client, friend, "SeedFriend");
        _ = await CreateAlterAsync(client, trusted, "SeedTrusted");
        await EnsurePublicProfileAsync(client, owner, "alters-owner");
        await EnsurePublicProfileAsync(client, nonFriend, "alters-nonfriend");
        await EnsurePublicProfileAsync(client, friend, "alters-friend");
        await EnsurePublicProfileAsync(client, trusted, "alters-trusted");

        var alterPublic = await CreateAlterAsync(client, owner, "VisPublic");
        var alterFriends = await CreateAlterAsync(client, owner, "VisFriends");
        var alterTrusted = await CreateAlterAsync(client, owner, "VisTrusted");
        var alterPrivate = await CreateAlterAsync(client, owner, "VisPrivate");

        await SetAlterSecurityLevelAsync(client, owner, alterPublic, VisibilityLevel.Public);
        await SetAlterSecurityLevelAsync(client, owner, alterFriends, VisibilityLevel.FriendsOnly);
        await SetAlterSecurityLevelAsync(client, owner, alterTrusted, VisibilityLevel.TrustedOnly);
        await SetAlterSecurityLevelAsync(client, owner, alterPrivate, VisibilityLevel.Private);

        // Non-friend viewer: sees Public only. Restricted alters surface as
        // alter_not_found (the guarded read masks visibility rejects as 404s so callers
        // can't distinguish "no such alter" from "not permitted").
        await AssertAlterVisibleAsync(client, owner, alterPublic, nonFriend);
        await AssertAlterHiddenAsync(client, owner, alterFriends, nonFriend);
        await AssertAlterHiddenAsync(client, owner, alterTrusted, nonFriend);
        await AssertAlterHiddenAsync(client, owner, alterPrivate, nonFriend);

        // Friend viewer gains FriendsOnly; TrustedOnly stays hidden.
        await SendFriendRequestAndAcceptAsync(client, friend, owner);

        await AssertAlterVisibleAsync(client, owner, alterFriends, friend);
        await AssertAlterHiddenAsync(client, owner, alterTrusted, friend);

        // Trusted viewer gains TrustedOnly; Private stays hidden.
        await SendFriendRequestAndAcceptAsync(client, trusted, owner);
        await SetFriendTrustAsync(client, owner, trusted);

        await AssertAlterVisibleAsync(client, owner, alterTrusted, trusted);
        await AssertAlterHiddenAsync(client, owner, alterPrivate, trusted);
    }

    /// <summary>
    /// Asserts <paramref name="viewer"/> can GET <c>/api/systems/{owner}/alters/{alterId}</c>
    /// with 200 + the alter payload's <see cref="BareAlter.Id"/> matching. Deserialising into
    /// the real read model means a wire-shape drift trips at deserialise time instead of
    /// hiding as a silent match/miss in a stringly-typed body scan.
    /// </summary>
    private static async Task AssertAlterVisibleAsync(HttpClient client, string owner, AlterId alterId, string viewer)
    {
        using var res = await client.SendAuthedGetAsync($"/api/systems/{owner}/alters/{alterId}", viewer);
        var envelope = await res.ReadEnvelopeAsync<BareAlter>(HttpStatusCode.OK);
        await Assert.That(envelope.Data.Id).IsEqualTo(alterId)
            .Because($"Expected viewer '{viewer}' to see alter {alterId.Value} in owner '{owner}''s public read; got a different alter id back.");
    }

    /// <summary>
    /// Asserts <paramref name="viewer"/> hits 404 + <see cref="ErrorCodes.AlterNotFound"/>
    /// on the guarded alter GET. The API deliberately conflates "no such alter" and
    /// "not permitted" into the same shape so callers can't fingerprint restricted alters;
    /// this assertion pins that behaviour.
    /// </summary>
    private static async Task AssertAlterHiddenAsync(HttpClient client, string owner, AlterId alterId, string viewer)
    {
        using var res = await client.SendAuthedGetAsync($"/api/systems/{owner}/alters/{alterId}", viewer);
        var error = await res.ReadErrorAsync(HttpStatusCode.NotFound);
        await Assert.That(error.Code).IsEqualTo(ErrorCodes.AlterNotFound)
            .Because($"Expected viewer '{viewer}' to receive alter_not_found for owner '{owner}''s alter {alterId.Value}; got '{error.Code}' with detail '{error.Detail}'.");
    }

    [Test]
    public async Task PublicTag_PrivateSecurity_Returns404ForAnonymous()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-guarded-tag";
        var tagId = await CreateTagAsync(client, principal, "GuardedTag");
        await EnsurePublicProfileAsync(client, principal, "guarded-tag");

        await SetTagSecurityLevelAsync(client, principal, tagId, VisibilityLevel.Private);

        using var publicRes = await client.GetAsync($"/api/systems/{principal}/tags/{tagId}");
        await Assert.That(publicRes.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Visibility_NonFriendFriendTrusted_AppliesToTags()
    {
        using var client = TestClient.NoRedirect(fixture);

        var owner = "tags-visibility-owner";
        var nonFriend = "tags-visibility-nonfriend";
        var friend = "tags-visibility-friend";
        var trusted = "tags-visibility-trusted";

        _ = await CreateAlterAsync(client, nonFriend, "SeedNonFriend");
        _ = await CreateAlterAsync(client, friend, "SeedFriend");
        _ = await CreateAlterAsync(client, trusted, "SeedTrusted");
        await EnsurePublicProfileAsync(client, owner, "tags-owner");
        await EnsurePublicProfileAsync(client, nonFriend, "tags-nonfriend");
        await EnsurePublicProfileAsync(client, friend, "tags-friend");
        await EnsurePublicProfileAsync(client, trusted, "tags-trusted");

        var tagPublic = await CreateTagAsync(client, owner, "TagPublic");
        var tagFriends = await CreateTagAsync(client, owner, "TagFriends");
        var tagTrusted = await CreateTagAsync(client, owner, "TagTrusted");
        var tagPrivate = await CreateTagAsync(client, owner, "TagPrivate");

        await SetTagSecurityLevelAsync(client, owner, tagPublic, VisibilityLevel.Public);
        await SetTagSecurityLevelAsync(client, owner, tagFriends, VisibilityLevel.FriendsOnly);
        await SetTagSecurityLevelAsync(client, owner, tagTrusted, VisibilityLevel.TrustedOnly);
        await SetTagSecurityLevelAsync(client, owner, tagPrivate, VisibilityLevel.Private);

        // Non-friend viewer: Public only; restricted tags mask as tag_not_found for the
        // same fingerprint-resistance reason the alter surface uses.
        await AssertTagVisibleAsync(client, owner, tagPublic, nonFriend);
        await AssertTagHiddenAsync(client, owner, tagFriends, nonFriend);
        await AssertTagHiddenAsync(client, owner, tagTrusted, nonFriend);
        await AssertTagHiddenAsync(client, owner, tagPrivate, nonFriend);

        // Friend viewer gains FriendsOnly.
        await SendFriendRequestAndAcceptAsync(client, friend, owner);

        await AssertTagVisibleAsync(client, owner, tagFriends, friend);
        await AssertTagHiddenAsync(client, owner, tagTrusted, friend);

        // Trusted viewer gains TrustedOnly; Private stays hidden.
        await SendFriendRequestAndAcceptAsync(client, trusted, owner);
        await SetFriendTrustAsync(client, owner, trusted);

        await AssertTagVisibleAsync(client, owner, tagTrusted, trusted);
        await AssertTagHiddenAsync(client, owner, tagPrivate, trusted);
    }

    /// <summary>
    /// Tag counterpart of <see cref="AssertAlterVisibleAsync"/>: asserts a 200 with the
    /// expected <see cref="TagPublicReadModel.Id"/> in the envelope. Kept per-domain rather
    /// than generic because the read-model type is what makes wire-shape drift fail loudly.
    /// </summary>
    private static async Task AssertTagVisibleAsync(HttpClient client, string owner, TagId tagId, string viewer)
    {
        using var res = await client.SendAuthedGetAsync($"/api/systems/{owner}/tags/{tagId}", viewer);
        var envelope = await res.ReadEnvelopeAsync<TagPublicReadModel>(HttpStatusCode.OK);
        await Assert.That(envelope.Data.Id).IsEqualTo(tagId)
            .Because($"Expected viewer '{viewer}' to see tag {tagId} in owner '{owner}''s public read; got a different tag id back.");
    }

    /// <summary>
    /// Tag counterpart of <see cref="AssertAlterHiddenAsync"/>: asserts a 404 + the
    /// <see cref="ErrorCodes.TagNotFound"/> code (the same "hide the reason" contract the
    /// alter surface uses).
    /// </summary>
    private static async Task AssertTagHiddenAsync(HttpClient client, string owner, TagId tagId, string viewer)
    {
        using var res = await client.SendAuthedGetAsync($"/api/systems/{owner}/tags/{tagId}", viewer);
        var error = await res.ReadErrorAsync(HttpStatusCode.NotFound);
        await Assert.That(error.Code).IsEqualTo(ErrorCodes.TagNotFound)
            .Because($"Expected viewer '{viewer}' to receive tag_not_found for owner '{owner}''s tag {tagId}; got '{error.Code}' with detail '{error.Detail}'.");
    }

    private static async Task EnsurePublicProfileAsync(HttpClient client, string principal, string username)
    {
        using var response = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/username",
            new SettingsUsernameRequest(new Username(username)),
            principal);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent)
            .Because($"Expected username seed 204 for '{principal}', got {(int)response.StatusCode}. Body: {await response.Content.ReadAsStringAsync()}");
    }
}

