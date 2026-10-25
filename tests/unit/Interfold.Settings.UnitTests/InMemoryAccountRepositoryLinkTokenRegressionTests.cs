using Interfold.Infrastructure.InMemory;
using Interfold.Infrastructure.Sqlite;
using Interfold.Settings.Contracts.Ids;
using Interfold.Shared.Contracts.Ids;
using Interfold.Infrastructure.Sqlite.Repository;
using Microsoft.Extensions.Time.Testing;

namespace Interfold.Api.UnitTests;

// Regression suite for InMemoryAccountRepository's link-token map. Each test pins a
// previously-latent failure mode: TTL parity, resolve-miss forward-scrub,
// and Compose-vs-hand-concat double-prefix on the reverse-map key.
public sealed class InMemoryAccountRepositoryLinkTokenRegressionTests
{
    private static readonly SystemId RawSystemId = new("abcdefg");
    private static readonly SystemId AlreadySystemId = new("nam:abcdefg");

    [Test]
    public async Task ResolveSystemIdByLinkTokenAsync_TokenPastTtl_ReturnsNull()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);

        var factory = InMemoryServiceCollectionExtensions.CreateIsolatedConnectionFactory();
        var encryption = new SqliteEncryptionStateRepository(factory, clock);
        var repo = new SqliteAccountRepository(factory, encryption, clock);

        await repo.EnsureExistsAsync(RawSystemId);
        var token = await repo.GetOrCreateLinkTokenAsync(RawSystemId);

        clock.Advance(LinkToken.Ttl + TimeSpan.FromSeconds(1));

        var resolved = await repo.ResolveSystemIdByLinkTokenAsync(token);
        await Assert.That(resolved).IsNull()
            .Because("Bug A regression: a stale token past the 5-minute TTL must not resolve — otherwise the InMemory adapter diverges from the expiry contract.");
    }

    [Test]
    public async Task GetLinkTokenAsync_TokenPastTtl_ReturnsNull()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);

        var factory = InMemoryServiceCollectionExtensions.CreateIsolatedConnectionFactory();
        var encryption = new SqliteEncryptionStateRepository(factory, clock);
        var repo = new SqliteAccountRepository(factory, encryption, clock);

        await repo.EnsureExistsAsync(RawSystemId);
        await repo.GetOrCreateLinkTokenAsync(RawSystemId);
        clock.Advance(LinkToken.Ttl + TimeSpan.FromSeconds(1));

        var read = await repo.GetLinkTokenAsync(RawSystemId);
        await Assert.That(read).IsNull()
            .Because("Bug A regression: GetLinkTokenAsync must respect the same 5-minute TTL as ResolveSystemIdByLinkTokenAsync so a client-side check cannot diverge from the server-side gate.");
    }

    // Deterministic-hash tokens produce the same reverse-map key for the same systemId;
    // a resolve miss must scrub the forward pointer so the next GetOrCreate re-issues
    // rather than adopting a dangling reverse-map pointer.
    [Test]
    public async Task ResolveSystemIdByLinkTokenAsync_ExpiredMiss_ScrubsForwardPointer()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);

        var factory = InMemoryServiceCollectionExtensions.CreateIsolatedConnectionFactory();
        var encryption = new SqliteEncryptionStateRepository(factory, clock);
        var repo = new SqliteAccountRepository(factory, encryption, clock);

        await repo.EnsureExistsAsync(RawSystemId);
        var originalToken = await repo.GetOrCreateLinkTokenAsync(RawSystemId);
        clock.Advance(LinkToken.Ttl + TimeSpan.FromSeconds(1));

        var resolvedAfterExpiry = await repo.ResolveSystemIdByLinkTokenAsync(originalToken);
        await Assert.That(resolvedAfterExpiry).IsNull()
            .Because("Setup for bug B: the expired token must first be rejected so the scrub branch fires.");

        var stalePointer = await repo.GetLinkTokenAsync(RawSystemId);
        await Assert.That(stalePointer).IsNull()
            .Because("Bug B regression: a resolve-side miss must scrub the forward pointer so GetLinkTokenAsync doesn't hand out a token whose reverse-map entry was just refused.");
    }

    // Reverse-map key must go through new SystemId, not hand-concat — otherwise
    // an already-scoped input becomes "nam:nam:abcdefg" and ClearLinkTokenAsync misses.
    [Test]
    public async Task GetOrCreateLinkTokenAsync_AlreadyScopedInput_NoDoublePrefix_AndClearRoundTrips()
    {
        var factory = InMemoryServiceCollectionExtensions.CreateIsolatedConnectionFactory();
        var encryption = new SqliteEncryptionStateRepository(factory, TimeProvider.System);
        var repo = new SqliteAccountRepository(factory, encryption, TimeProvider.System);

        await repo.EnsureExistsAsync(AlreadySystemId);
        var issuedToken = await repo.GetOrCreateLinkTokenAsync(AlreadySystemId);
        var resolved = await repo.ResolveSystemIdByLinkTokenAsync(issuedToken);

        await Assert.That(resolved).IsNotNull()
            .Because("Bug C regression: a scoped-input token must resolve to the same single-prefixed id.");
        await Assert.That(resolved!.Value.Value).IsEqualTo("nam:abcdefg")
            .Because("The resolved id must be single-prefixed. Any double-prefix (e.g. \"nam:nam:abcdefg\") indicates the hand-concat pattern crept back in.");

        var cleared = await repo.ClearLinkTokenAsync(AlreadySystemId);
        await Assert.That(cleared).IsTrue()
            .Because("ClearLinkTokenAsync must succeed for the same scoped input that GetOrCreate accepted.");

        var postClear = await repo.ResolveSystemIdByLinkTokenAsync(issuedToken);
        await Assert.That(postClear).IsNull()
            .Because("Bug C regression: the clear path and the write path must use the same Compose call, otherwise a double-prefixed key survives ClearLinkTokenAsync because the two sides disagree on the key shape.");
    }
}
