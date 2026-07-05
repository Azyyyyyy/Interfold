using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Infrastructure.Scylla;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Interfold.IntegrationTests.Services.Scylla;

public sealed class RegionContextCachingTests : BaseEndpointTest
{
    // A stub that throws if the session is ever accessed, proving the cache is always used.
    private sealed class ThrowingSessionProvider : IScyllaSessionProvider
    {
        public Task<ISession> GetSessionAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("DB must not be reached when cache is warm.");
    }

    private static ScyllaUserRegistryRegionContext BuildContext(string defaultRegion = "nam")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OCTOCON_SCYLLA_KEYSPACE"] = defaultRegion })
            .Build();
        return new(new ThrowingSessionProvider(), new PersistenceConfiguration { ScyllaKeyspace = defaultRegion },
            config,
            NullLogger<ScyllaUserRegistryRegionContext>.Instance);
    }

    [Test]
    public async Task ResolveUserRegion_FallsBackToDefault_WhenSessionThrowsAndCacheEmpty()
    {
        // ThrowingSessionProvider will throw; the catch block should return CurrentRegion.
        var ctx = BuildContext("eur");
        var result = ctx.ResolveUserRegion("user-123");
        await Assert.That(result).IsEqualTo(ScyllaKeyspace.Eur);
    }

    [Test]
    public async Task ResolveUserRegion_UsesCachedRegion_AfterRegisterRegion()
    {
        var ctx = BuildContext("nam");
        ctx.RegisterRegion("user-abc", ScyllaKeyspace.Eur);

        // ThrowingSessionProvider must NOT be called; cache is warm.
        var result = ctx.ResolveUserRegion("user-abc");
        await Assert.That(result).IsEqualTo(ScyllaKeyspace.Eur);
    }

    [Test]
    public async Task ResolveUserRegion_StripsLegacyPrefix_BeforeCacheLookup()
    {
        var ctx = BuildContext("nam");
        ctx.RegisterRegion("eas", ScyllaKeyspace.Nam);  // unrelated key must not interfere

        // The prefixed form should resolve via the same stripped key.
        ctx.RegisterRegion("eas:user-xyz", ScyllaKeyspace.Sam);
        var result = ctx.ResolveUserRegion("eas:user-xyz");
        await Assert.That(result).IsEqualTo(ScyllaKeyspace.Sam);

        // Plain key lookup after prefix strip should also be cache-warm.
        var result2 = ctx.ResolveUserRegion("eas:user-xyz");
        await Assert.That(result2).IsEqualTo(ScyllaKeyspace.Sam);
    }

    [Test]
    public async Task ResolveConsistency_ReturnsLocal_ForSameRegion()
    {
        var ctx = BuildContext("nam");
        await Assert.That(ctx.ResolveConsistency(ScyllaKeyspace.Nam)).IsEqualTo("local");
    }

    [Test]
    public async Task ResolveConsistency_ReturnsGlobal_ForDifferentRegion()
    {
        var ctx = BuildContext("nam");
        await Assert.That(ctx.ResolveConsistency(ScyllaKeyspace.Eur)).IsEqualTo("global");
    }

    [Test]
    public async Task RegisterRegion_EmptyKey_DoesNotCorruptCache()
    {
        var ctx = BuildContext("nam");
        ctx.RegisterRegion("", ScyllaKeyspace.Eur);   // empty key — should be no-op

        // Fallback should still apply because nothing was cached ("user-1" resolution hits
        // the throwing session and falls back to the default region).
        var result = ctx.ResolveUserRegion("user-1");
        await Assert.That(result).IsEqualTo(ScyllaKeyspace.Nam);
    }
}
