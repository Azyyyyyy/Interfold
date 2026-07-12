using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.InMemory.Repository;
using TUnit.Mocks;

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
/// The Discord dispatch branch is exercised against a strict TUnit.Mocks stand-in for
/// <see cref="IAccountRepository"/>: any accidental hit on an unrelated member throws
/// <c>MockStrictBehaviorException</c> naming the method, replacing the earlier hand-
/// rolled fake's blanket <see cref="NotSupportedException"/> guard. The friendship repo's
/// <c>Kind.Discord</c> branch delegates to the find-only
/// <see cref="IAccountRepository.TryFindSystemIdByDiscordIdAsync"/> — the split is why we
/// can pin the "unknown Discord id → null" contract without polluting the account store.
/// </para>
/// </summary>
public sealed class ResolveUserIdDispatchTests
{
    // ---------------- Discord dispatch --------------------------------------

    [Test]
    public async Task ResolveUserId_DiscordPrefix_DelegatesToAccountRepo_AndPropagatesHit()
    {
        SystemId expected = new("nam:abcdefg");
        var accounts = IAccountRepository.Mock(MockBehavior.Strict);
        // TUnit.Mocks auto-unwraps Task<T> return types, so the setup takes the inner
        // SystemId? directly rather than a wrapped Task.FromResult.
        accounts.TryFindSystemIdByDiscordIdAsync(Any(), Any())
            .Returns((SystemId?)expected);
        var repo = new InMemoryFriendshipRepository(accounts);

        var resolved = await repo.ResolveUserIdAsync(new("discord:1234567890"));

        using (Assert.Multiple())
        {
            await Assert.That(resolved).IsEqualTo(expected)
                .Because("The Kind.Discord branch must propagate whatever the account repo returns verbatim so the caller sees the same scoped id shape they'd get from the OAuth-login flow.");
            // Verification carries the same intent as the old
            // `RecordedDiscordLookups.IsEquivalentTo([new DiscordId("1234567890")])` check:
            // the dispatch must hand the RawId (after-colon) to the account repo, not the
            // full 'discord:...' string — otherwise the Discord snowflake would be looked
            // up as a literal 'discord:1234567890'.
            accounts.TryFindSystemIdByDiscordIdAsync(new DiscordId("1234567890"), Any())
                .WasCalled(Times.Once);
        }
    }

    [Test]
    public async Task ResolveUserId_DiscordPrefix_UnknownId_ReturnsNull_NoPhantomCreate()
    {
        // Routing to TryFindSystemIdByDiscordIdAsync (rather than the auto-provisioning
        // FindOrCreateSystemIdAsync) is deliberate — unknown Discord ids must NOT spawn
        // phantom accounts. A null return becomes a "no such user" upstream. The strict
        // mock enforces this: if the friendship repo ever routed to FindOrCreate here,
        // the test would fail with MockStrictBehaviorException naming the wrong method.
        var accounts = IAccountRepository.Mock(MockBehavior.Strict);
        accounts.TryFindSystemIdByDiscordIdAsync(Any(), Any())
            .Returns((SystemId?)null);
        var repo = new InMemoryFriendshipRepository(accounts);

        var resolved = await repo.ResolveUserIdAsync(new("discord:9999999999999"));

        await Assert.That(resolved).IsNull()
            .Because("An unknown Discord id must surface as null instead of spawning a phantom account — that's the whole reason the Discord branch delegates to TryFind rather than FindOrCreate.");
    }

    // ---------------- Username dispatch (InMemory has no reverse index) -----

    [Test]
    public async Task ResolveUserId_UsernamePrefix_ReturnsNull_AccountRepoNotConsulted()
    {
        // InMemory has no users_by_username table. Returning SystemId("username:alice")
        // verbatim (treating the literal string as a system id) is the exact footgun
        // LookupHandle prevents. The strict mock has no members configured — any hop
        // into the account repo would fail loudly with MockStrictBehaviorException.
        var accounts = IAccountRepository.Mock(MockBehavior.Strict);
        var repo = new InMemoryFriendshipRepository(accounts);

        var resolved = await repo.ResolveUserIdAsync(new("username:alice"));

        using (Assert.Multiple())
        {
            await Assert.That(resolved).IsNull()
                .Because("Username lookup has no InMemory reverse index; the correct answer is 'no such user' rather than fabricating a SystemId('username:alice').");
            // The Username branch must not hop into the account repo — a spurious call
            // would leak dispatch intent across kinds. Belt-and-braces alongside the
            // strict-mock guard above.
            accounts.TryFindSystemIdByDiscordIdAsync(Any(), Any()).WasCalled(Times.Never);
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

        var resolved = await repo.ResolveUserIdAsync(new(input));

        await Assert.That(resolved?.Value).IsEqualTo(expected)
            .Because($"'{input}' must normalise to '{expected}' so the resolved id can be used as a storage key for the other InMemory repos (which write with region-stripped keys).");
    }

    // ---------------- Unparseable / unknown prefix --------------------------

    [Test]
    public async Task ResolveUserId_UnknownPrefix_ReturnsNull()
    {
        // "xxx" is neither a region tag nor an id/username/discord discriminator, so
        // LookupHandle.TryParse rejects it and dispatch takes the "unparseable" branch.
        // The .Because below states why null (not opaque round-trip) is the correct
        // answer; see InMemoryFriendshipRepository.ResolveUserIdAsync for the full
        // asymmetry with the Scylla user_registry path.
        var repo = new InMemoryFriendshipRepository();

        var resolved = await repo.ResolveUserIdAsync(new("xxx:abcdefg"));

        await Assert.That(resolved).IsNull()
            .Because("Unknown non-region prefix must return null on the InMemory backend so the command handler surfaces the same friend_request:no_user (422) the Scylla path produces via user_registry miss — opaque round-trip is the Scylla-only path, not a portable InMemory pin.");
    }

    [Test]
    public async Task ResolveUserId_BlankInput_ReturnsNull()
    {
        var repo = new InMemoryFriendshipRepository();
        await Assert.That(await repo.ResolveUserIdAsync(new(""))).IsNull();
        await Assert.That(await repo.ResolveUserIdAsync(new("   "))).IsNull();
    }
}
