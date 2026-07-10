using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.InMemory.Repository;

namespace Interfold.Api.UnitTests.Friendships;

/// <summary>
/// Pins the <see cref="LookupHandle"/>-driven dispatch matrix on
/// <see cref="InMemoryFriendshipRepository.ResolveUserIdAsync"/>. The Scylla dispatch
/// path exercises the same routing table, but the integration-level
/// <c>SendFriendRequestPrefixTests</c> covers that end-to-end — running the Scylla
/// dispatch through unit tests would require a fake CQL session which just re-implements
/// the same switch we're already testing here.
///
/// <para>
/// Uses a <see cref="RecordingAccountRepository"/> stub so the Discord dispatch branch
/// can be observed without pulling in the real InMemory account repo (which would auto-
/// create phantom accounts on miss via <c>FindOrCreateSystemIdAsync</c>). The friendship
/// repo's <c>Kind.Discord</c> branch delegates to the find-only
/// <see cref="IAccountRepository.TryFindSystemIdByDiscordIdAsync"/> — the split is why we
/// can test the "unknown Discord id → null" contract without polluting the account store.
/// </para>
/// </summary>
public sealed class ResolveUserIdDispatchTests
{
    // ---------------- Discord dispatch --------------------------------------

    [Test]
    public async Task ResolveUserId_DiscordPrefix_DelegatesToAccountRepo_AndPropagatesHit()
    {
        var expected = new SystemId("nam:abcdefg");
        var accounts = new RecordingAccountRepository
        {
            DiscordLookup = _ => expected,
        };
        var repo = new InMemoryFriendshipRepository(accounts);

        var resolved = await repo.ResolveUserIdAsync(new UsernameOrSystemId("discord:1234567890"));

        using (Assert.Multiple())
        {
            await Assert.That(resolved).IsEqualTo(expected)
                .Because("The Kind.Discord branch must propagate whatever the account repo returns verbatim so the caller sees the same scoped id shape they'd get from the OAuth-login flow.");
            await Assert.That(accounts.RecordedDiscordLookups).IsEquivalentTo(new[] { new DiscordId("1234567890") })
                .Because("The dispatch must hand the RawId (after-colon) to the account repo, not the full 'discord:...' string — otherwise the Discord snowflake would be looked up as a literal 'discord:1234567890'.");
        }
    }

    [Test]
    public async Task ResolveUserId_DiscordPrefix_UnknownId_ReturnsNull_NoPhantomCreate()
    {
        // Routing to TryFindSystemIdByDiscordIdAsync (rather than the auto-provisioning
        // FindOrCreateSystemIdAsync) is deliberate — unknown Discord ids must NOT spawn
        // phantom accounts. A null return becomes a "no such user" upstream.
        var accounts = new RecordingAccountRepository
        {
            DiscordLookup = _ => null,
        };
        var repo = new InMemoryFriendshipRepository(accounts);

        var resolved = await repo.ResolveUserIdAsync(new UsernameOrSystemId("discord:9999999999999"));

        await Assert.That(resolved).IsNull()
            .Because("An unknown Discord id must surface as null instead of spawning a phantom account — that's the whole reason the Discord branch delegates to TryFind rather than FindOrCreate.");
    }

    // ---------------- Username dispatch (InMemory has no reverse index) -----

    [Test]
    public async Task ResolveUserId_UsernamePrefix_ReturnsNull_AccountRepoNotConsulted()
    {
        // InMemory has no users_by_username table. Returning SystemId("username:alice")
        // verbatim (treating the literal string as a system id) is the exact footgun
        // LookupHandle prevents.
        var accounts = new RecordingAccountRepository();
        var repo = new InMemoryFriendshipRepository(accounts);

        var resolved = await repo.ResolveUserIdAsync(new UsernameOrSystemId("username:alice"));

        using (Assert.Multiple())
        {
            await Assert.That(resolved).IsNull()
                .Because("Username lookup has no InMemory reverse index; the correct answer is 'no such user' rather than fabricating a SystemId('username:alice').");
            await Assert.That(accounts.RecordedDiscordLookups).IsEmpty()
                .Because("The Username branch must not hop into the account repo — a spurious call would leak dispatch intent across kinds.");
        }
    }

    // ---------------- Id / Region / bare-id dispatch ------------------------

    [Test]
    [Arguments("id:abcdefg",  "abcdefg")]
    [Arguments("nam:abcdefg", "abcdefg")]
    [Arguments("gdpr:xyz1234", "xyz1234")]
    [Arguments("abcdefg",      "abcdefg")]
    public async Task ResolveUserId_IdOrRegionOrBare_NormalizesToRawId(string input, string expected)
    {
        var repo = new InMemoryFriendshipRepository();

        var resolved = await repo.ResolveUserIdAsync(new UsernameOrSystemId(input));

        await Assert.That(resolved?.Value).IsEqualTo(expected)
            .Because($"'{input}' must normalise to '{expected}' so the resolved id can be used as a storage key for the other InMemory repos (which write with region-stripped keys).");
    }

    // ---------------- Unparseable / unknown prefix --------------------------

    [Test]
    public async Task ResolveUserId_UnknownPrefix_ReturnsNull()
    {
        // "xxx:abcdefg" has an unknown non-region prefix (xxx is neither a region tag
        // nor one of the id/username/discord discriminator prefixes). LookupHandle.
        // TryParse rejects it, so the InMemory dispatch takes the "unparseable" branch.
        //
        // The correct InMemory behaviour is "return null" — NOT "opaque round-trip via
        // Normalize". The Scylla equivalent round-trips the whole input into a
        // user_registry.user_id lookup which misses → NoUser → friend_request:no_user
        // (422). InMemory has no user_registry, so a round-trip here would flow the
        // opaque id straight into the friend-request writer with no existence check,
        // creating a phantom friend request under the "xxx:abcdefg" key and returning
        // 204 NoContent instead of the 422 that the cross-backend contract requires
        // (pinned by SendFriendRequestPrefixTests.SendFriendRequest_UnknownPrefix_
        // ReturnsNoUser). See InMemoryFriendshipRepository.ResolveUserIdAsync's
        // rationale block for the full backend-shape asymmetry.
        var repo = new InMemoryFriendshipRepository();

        var resolved = await repo.ResolveUserIdAsync(new UsernameOrSystemId("xxx:abcdefg"));

        await Assert.That(resolved).IsNull()
            .Because("Unknown non-region prefix must return null on the InMemory backend so the command handler surfaces the same friend_request:no_user (422) the Scylla path produces via user_registry miss — opaque round-trip is the Scylla-only path, not a portable InMemory pin.");
    }

    [Test]
    public async Task ResolveUserId_BlankInput_ReturnsNull()
    {
        var repo = new InMemoryFriendshipRepository();
        await Assert.That(await repo.ResolveUserIdAsync(new UsernameOrSystemId(""))).IsNull();
        await Assert.That(await repo.ResolveUserIdAsync(new UsernameOrSystemId("   "))).IsNull();
    }

    // ---------------- Recording fake ----------------------------------------

    private sealed class RecordingAccountRepository : IAccountRepository
    {
        public List<DiscordId> RecordedDiscordLookups { get; } = new();
        public Func<DiscordId, SystemId?>? DiscordLookup { get; set; }

        public Task<SystemId?> TryFindSystemIdByDiscordIdAsync(DiscordId discordId, CancellationToken cancellationToken = default)
        {
            RecordedDiscordLookups.Add(discordId);
            return Task.FromResult(DiscordLookup?.Invoke(discordId));
        }

        // Every other member throws — the fake is deliberately narrow so a future test
        // that spuriously exercises an unrelated method fails loudly instead of silently
        // succeeding on a default-value stub.
        public Task<bool> UpdateUsernameAsync(SystemId systemId, Username username, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdateDescriptionAsync(SystemId systemId, string description, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdateAvatarAsync(SystemId systemId, AvatarUrl avatarUrl, AvatarSource source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ClearAvatarAsync(SystemId systemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LinkToken> GetOrCreateLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LinkToken?> GetLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SystemId?> ResolveSystemIdByLinkTokenAsync(LinkToken linkToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ClearLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        // ProviderIdentity-keyed consolidation of the account repo interface — the two
        // methods below replace six per-provider Find* / Link* stubs. The test only
        // exercises TryFindSystemIdByDiscordIdAsync, so every other member throws.
        public Task<SystemId?> FindOrCreateSystemIdAsync(ProviderIdentity identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountLinkResult> LinkIdentityToUserAsync(SystemId systemId, ProviderIdentity identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UnlinkDiscordAsync(SystemId systemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UnlinkEmailAsync(SystemId systemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UnlinkAppleAsync(SystemId systemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(SystemId systemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(SystemId systemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
