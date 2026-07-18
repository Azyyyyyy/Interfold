using System.Net;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class AltersControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task FieldSecurityLevelByRelationship_AppliesCorrectly()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var owner = "parity-guarded-fields-owner";
        var nonFriend = "parity-guarded-fields-nonfriend";
        var friend = "parity-guarded-fields-friend";
        var trusted = "parity-guarded-fields-trusted";

        await EnsureUserExistsAsync(client, owner);
        _ = await CreateAlterAsync(client, nonFriend, "SeedNonFriend");
        _ = await CreateAlterAsync(client, friend, "SeedFriend");
        _ = await CreateAlterAsync(client, trusted, "SeedTrusted");

        var fieldPublic = await CreateSettingsFieldAsync(client, owner, "FieldPublic", FieldType.Text, VisibilityLevel.Public);
        var fieldFriends = await CreateSettingsFieldAsync(client, owner, "FieldFriends", FieldType.Text, VisibilityLevel.FriendsOnly);
        var fieldTrusted = await CreateSettingsFieldAsync(client, owner, "FieldTrusted", FieldType.Text, VisibilityLevel.TrustedOnly);
        var fieldPrivate = await CreateSettingsFieldAsync(client, owner, "FieldPrivate", FieldType.Text, VisibilityLevel.Private);

        var alterId = await CreateAlterAsync(client, owner, "GuardedFieldsAlter");
        await SetAlterSecurityLevelAsync(client, owner, alterId, VisibilityLevel.Public);
        await UpdateAlterFieldsAsync(client, owner, alterId, new UpdateAlterFieldRequest[]
        {
            new(fieldPublic, "PublicValue"),
            new(fieldFriends, "FriendsValue"),
            new(fieldTrusted, "TrustedValue"),
            new(fieldPrivate, "PrivateValue"),
        });

        using var nonFriendRes = await client.SendAuthedGetAsync($"/api/systems/{owner}/alters/{alterId}", friend);
        var nonFriendBody = await nonFriendRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(nonFriendRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(nonFriendBody).Contains("PublicValue");
            await Assert.That(nonFriendBody).DoesNotContain("FriendsValue");
            await Assert.That(nonFriendBody).DoesNotContain("TrustedValue");
            await Assert.That(nonFriendBody).DoesNotContain("PrivateValue");
        }

        await SendFriendRequestAndAcceptAsync(client, friend, owner);

        using var friendRes = await client.SendAuthedGetAsync($"/api/systems/{owner}/alters/{alterId}", friend);
        var friendBody = await friendRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(friendRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(friendBody).Contains("PublicValue");
            await Assert.That(friendBody).Contains("FriendsValue");
            await Assert.That(friendBody).DoesNotContain("TrustedValue");
            await Assert.That(friendBody).DoesNotContain("PrivateValue");
        }

        await SendFriendRequestAndAcceptAsync(client, trusted, owner);
        await SetFriendTrustAsync(client, owner, trusted);

        using var trustedRes = await client.SendAuthedGetAsync($"/api/systems/{owner}/alters/{alterId}", trusted);
        var trustedBody = await trustedRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(trustedRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(trustedBody).Contains("PublicValue");
            await Assert.That(trustedBody).Contains("FriendsValue");
            await Assert.That(trustedBody).Contains("TrustedValue");
            await Assert.That(trustedBody).DoesNotContain("PrivateValue");
        }
    }

    [Test]
    public async Task CustomFields_FieldSecurityLevelByRelationship_AppliesCorrectly()
    {
        using var client = fixture.Factory.CreateClient();

        var owner = "settings-guarded-fields-owner";
        var nonFriend = "settings-guarded-fields-nonfriend";
        var friend = "settings-guarded-fields-friend";
        var trusted = "settings-guarded-fields-trusted";

        await EnsureUserExistsAsync(client, owner);
        _ = await CreateAlterAsync(client, nonFriend, "SeedNonFriend");
        _ = await CreateAlterAsync(client, friend, "SeedFriend");
        _ = await CreateAlterAsync(client, trusted, "SeedTrusted");

        var fieldPublic = await CreateSettingsFieldAsync(client, owner, "FieldPublic", FieldType.Text, VisibilityLevel.Public);
        var fieldFriends = await CreateSettingsFieldAsync(client, owner, "FieldFriends", FieldType.Text, VisibilityLevel.FriendsOnly);
        var fieldTrusted = await CreateSettingsFieldAsync(client, owner, "FieldTrusted", FieldType.Text, VisibilityLevel.TrustedOnly);
        var fieldPrivate = await CreateSettingsFieldAsync(client, owner, "FieldPrivate", FieldType.Text, VisibilityLevel.Private);

        var alterId = await CreateAlterAsync(client, owner, "GuardedFieldsAlter");
        await SetAlterSecurityLevelAsync(client, owner, alterId, VisibilityLevel.Public);
        await UpdateAlterFieldsAsync(client, owner, alterId, new UpdateAlterFieldRequest[]
        {
            new(fieldPublic, "PublicValue"),
            new(fieldFriends, "FriendsValue"),
            new(fieldTrusted, "TrustedValue"),
            new(fieldPrivate, "PrivateValue"),
        });

        using var nonFriendRes = await client.SendAuthedGetAsync($"/api/systems/{owner}/alters/{alterId}", nonFriend);
        var nonFriendBody = await nonFriendRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(nonFriendRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(nonFriendBody).Contains("PublicValue");
            await Assert.That(nonFriendBody).DoesNotContain("FriendsValue");
            await Assert.That(nonFriendBody).DoesNotContain("TrustedValue");
            await Assert.That(nonFriendBody).DoesNotContain("PrivateValue");
        }

        await SendFriendRequestAndAcceptAsync(client, friend, owner);

        using var friendRes = await client.SendAuthedGetAsync($"/api/systems/{owner}/alters/{alterId}", friend);
        var friendBody = await friendRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(friendRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(friendBody).Contains("PublicValue");
            await Assert.That(friendBody).Contains("FriendsValue");
            await Assert.That(friendBody).DoesNotContain("TrustedValue");
            await Assert.That(friendBody).DoesNotContain("PrivateValue");
        }

        await SendFriendRequestAndAcceptAsync(client, trusted, owner);
        await SetFriendTrustAsync(client, owner, trusted);

        using var trustedRes = await client.SendAuthedGetAsync($"/api/systems/{owner}/alters/{alterId}", trusted);
        var trustedBody = await trustedRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(trustedRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(trustedBody).Contains("PublicValue");
            await Assert.That(trustedBody).Contains("FriendsValue");
            await Assert.That(trustedBody).Contains("TrustedValue");
            await Assert.That(trustedBody).DoesNotContain("PrivateValue");
        }
    }



    [Test]
    public async Task AlterDelete_CascadesAlterJournalsAndDetachesFromGlobalJournals()
    {
        // Regression: deleting an alter must wipe its alter_journals entries (both view tables
        // in the Scylla repo) and detach it from any global_journal_alters rows, while leaving
        // the global journal itself intact (multiple alters can share a group journal).
        // Before the fix, DeleteAlterCommandHandler called only IAlterRepository.DeleteAsync and
        // left every alter-journal entry orphaned in the database.
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = $"parity-alter-delete-cascade-{Guid.NewGuid():N}"[..32];
        var deletedAlterId = await CreateAlterAsync(client, principal, "DeleteMe");
        var keeperAlterId = await CreateAlterAsync(client, principal, "KeepMe");

        // Create an alter-journal entry owned by the alter we're about to delete. After the
        // delete, the per-entry GET must 404 (the cascade wiped it).
        using var alterJournalRes = await client.SendAsJsonAsync(
            HttpMethod.Post, $"/api/systems/me/alters/{deletedAlterId}/journals",
            new CreateAlterJournalRequest("AlterJournalToCascade"),
            principal);
        var alterJournalEnv = await alterJournalRes.ReadEnvelopeAsync<AlterJournalReadModel>(HttpStatusCode.Created);
        var alterJournalEntryId = alterJournalEnv.Data.Id;

        // Create a global journal and attach BOTH alters. After the cascade, the global
        // journal must still exist with the keeper alter still attached.
        using var globalRes = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/journals",
            new CreateGlobalJournalRequest("GroupJournalShared"),
            principal);
        var globalEnv = await globalRes.ReadEnvelopeAsync<JournalReadModel>(HttpStatusCode.Created);
        var globalJournalId = globalEnv.Data.Id;

        foreach (var alterIdToAttach in new[] { deletedAlterId, keeperAlterId })
        {
            using var attachRes = await client.SendAsJsonAsync(
                HttpMethod.Post, $"/api/journals/{globalJournalId}/alter",
                new JournalAlterRequest(alterIdToAttach),
                principal);
            await Assert.That(attachRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        // Confirm setup: both alters attached before the delete.
        using var preDeleteRes = await client.SendAuthedGetAsync($"/api/journals/{globalJournalId}", principal);
        var preDeleteEnv = await preDeleteRes.ReadEnvelopeAsync<JournalReadModel>(HttpStatusCode.OK);
        using (Assert.Multiple())
        {
            await Assert.That(preDeleteEnv.Data.Alters).Contains(deletedAlterId);
            await Assert.That(preDeleteEnv.Data.Alters).Contains(keeperAlterId);
        }

        using var deleteAlterReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/systems/me/alters/{deletedAlterId}");
        AttachPrincipalAuth(deleteAlterReq, client, principal);
        using var deleteAlterRes = await client.SendAsync(deleteAlterReq);
        await Assert.That(deleteAlterRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // Cascade 1: the per-alter journal entry is gone.
        using var showAlterJournalRes = await client.SendAuthedGetAsync($"/api/systems/me/alters/journals/{alterJournalEntryId}", principal);
        await Assert.That(showAlterJournalRes.StatusCode)
            .IsEqualTo(HttpStatusCode.NotFound)
            .Because("the alter's journal entry should be cascade-deleted along with the alter.");

        // Cascade 2: the global journal still exists, but the deleted alter is no longer in
        // its alter_ids list. The keeper alter must still be attached.
        using var postDeleteRes = await client.SendAuthedGetAsync($"/api/journals/{globalJournalId}", principal);
        var postDeleteEnv = await postDeleteRes.ReadEnvelopeAsync<JournalReadModel>(HttpStatusCode.OK);
        using (Assert.Multiple())
        {
            await Assert.That(postDeleteEnv.Data.Alters).DoesNotContain(deletedAlterId);
            await Assert.That(postDeleteEnv.Data.Alters).Contains(keeperAlterId);
        }
    }


    [Test]
    public async Task AlterCreate_IdempotentReplay_WorksAgainstLiveAdapters()
    {
        using var client = fixture.Factory.CreateClient();

        var systemId = $"itest-{Guid.NewGuid():N}"[..14];
        var idempotencyKey = Guid.NewGuid().ToString("N");

        using var firstRes = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/systems/me/alters",
            new CreateAlterRequest("IntegrationSmoke"),
            systemId,
            idempotencyKey: idempotencyKey);
        var firstEnv = await firstRes.ReadEnvelopeAsync<AlterReadModel>(HttpStatusCode.Created);
        await Assert.That(firstEnv.Replay.GetValueOrDefault()).IsFalse();

        using var secondRes = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/systems/me/alters",
            new CreateAlterRequest("IntegrationSmoke"),
            systemId,
            idempotencyKey: idempotencyKey);
        var secondEnv = await secondRes.ReadEnvelopeAsync<AlterReadModel>(HttpStatusCode.Created);
        await Assert.That(secondEnv.Replay).IsTrue();
    }

    [Test]
    public async Task OperationalHealth_GuardedPaths_GetGuardedAsync_Succeeds()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // Query a non-existent alter should return 404, not 500
        var principal = "operational-health-test";
        using var res = await client.SendAuthedGetAsync("/api/systems/me/alters/9999", principal);

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Idempotency_AlterCreate_ReplayStable()
    {
        await RunSoakAsync(fixture.Factory, async (client, key) =>
        {
            return await client.SendAsJsonAsync(
                HttpMethod.Post, "/api/systems/me/alters",
                new CreateAlterRequest("SoakAlter"),
                "soak-default-principal",
                idempotencyKey: key);
        });
    }
}

