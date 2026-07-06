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
/// create phantom accounts on miss via <c>FindOrCreateSystemIdByDiscordIdAsync</c>). The
/// friendship repo's <c>Kind.Discord</c> branch delegates to the find-only
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
        // The whole point of routing to TryFindSystemIdByDiscordIdAsync (not
        // FindOrCreateSystemIdByDiscordIdAsync) is that unknown Discord ids must NOT
        // spawn phantom accounts. A null return here becomes a "no such user" response
        // upstream, matching how the friendship flow behaves for any other unknown handle.
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
        // InMemory has no users_by_username table. The pre-Slice-7 behaviour would have
        // returned SystemId("username:alice") verbatim (i.e. treated the literal string
        // as a system id), which is the exact footgun LookupHandle is here to prevent.
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
    public async Task ResolveUserId_UnknownPrefix_KeepsWholeInput()
    {
        // "xxx:abcdefg" has an unknown non-region prefix. LookupHandle.TryParse rejects
        // it, so the InMemory dispatch falls through to the raw-normalise branch with
        // the whole input. Normalize is a no-op on non-region prefixes (xxx isn't one
        // of the seven region tags), so the resolved value keeps its literal shape —
        // this preserves the "opaque bare id" behaviour the pre-Slice-7 fallback would
        // have delivered.
        var repo = new InMemoryFriendshipRepository();

        var resolved = await repo.ResolveUserIdAsync(new UsernameOrSystemId("xxx:abcdefg"));

        await Assert.That(resolved?.Value).IsEqualTo("xxx:abcdefg")
            .Because("Unknown non-region prefix must NOT be stripped — the value round-trips as an opaque handle so the caller can decide what to do with the miss.");
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
        public Task<SystemId?> FindOrCreateSystemIdByDiscordIdAsync(DiscordId discordId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SystemId?> FindSystemIdByEmailAsync(Email email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SystemId?> FindSystemIdByAppleIdAsync(AppleId appleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountLinkResult> LinkDiscordToUserAsync(SystemId systemId, DiscordId discordId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountLinkResult> LinkEmailToUserAsync(SystemId systemId, Email email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountLinkResult> LinkAppleToUserAsync(SystemId systemId, AppleId appleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UnlinkDiscordAsync(SystemId systemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UnlinkEmailAsync(SystemId systemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UnlinkAppleAsync(SystemId systemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(SystemId systemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(SystemId systemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
