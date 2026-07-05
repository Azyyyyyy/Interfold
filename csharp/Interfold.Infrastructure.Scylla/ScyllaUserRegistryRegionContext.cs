using System.Collections.Concurrent;
using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla;

/// <summary>
/// IRegionContext backed by global.user_registry, with a bounded in-process cache and
/// fallback to the locally configured default region when the registry row is absent or
/// the Scylla session is not yet available.
///
/// <para>
/// Public resolution APIs are typed as <see cref="ScyllaKeyspace"/>; the registry stores
/// lowercase region strings and the cache stays string-keyed with the legacy-prefix
/// stripping internal to this type.
/// </para>
/// </summary>
public sealed class ScyllaUserRegistryRegionContext : IRegionContext
{
    // Bounded LRU: keep most-recently-used entries simple via ConcurrentDictionary.
    // A higher-fidelity LRU eviction policy can be added later if memory pressure warrants it.
    private const int MaxCacheSize = 1024;

    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly PersistenceConfiguration _options;
    private readonly ILogger<ScyllaUserRegistryRegionContext> _logger;
    private readonly ConcurrentDictionary<string, ScyllaKeyspace> _cache = new(StringComparer.Ordinal);
    private readonly Lazy<ScyllaKeyspace> _currentRegion;

    public ScyllaKeyspace CurrentRegion => _currentRegion.Value;

    public ScyllaUserRegistryRegionContext(
        IScyllaSessionProvider sessionProvider,
        IOptions<PersistenceConfiguration> options,
        ILogger<ScyllaUserRegistryRegionContext> logger)
    {
        _sessionProvider = sessionProvider;
        _options = options.Value;
        _logger = logger;
        // PersistenceConfiguration.ScyllaKeyspace is the enum-typed single source of truth
        // for the per-node region identity; the resolver's GetKeyspace() call would parse
        // the same wire value back to this enum, so read it directly and skip the round-trip.
        _currentRegion = new Lazy<ScyllaKeyspace>(() => _options.ScyllaKeyspace);
    }

    public ScyllaKeyspace ResolveUserRegion(SystemId systemId) => ResolveUserRegion(systemId.Value);

    public ScyllaKeyspace ResolveUserRegion(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return CurrentRegion;

        // Slice 4: split off the raw id + any discriminator prefix ("username" / "discord" / a
        // region tag) up front. Region-scoped ids parse cleanly via ScopedSystemId; the
        // discriminator prefixes (which are NOT regions) fall through to the generic-split path.
        var (raw, prefix) = SplitDiscriminatorPrefix(systemId);

        if (_cache.TryGetValue(raw, out var cached))
            return cached;

        // Synchronous path: attempt a best-effort lookup on the calling thread.
        // This keeps the interface non-async while avoiding a thread-pool deadlock on most
        // callers that are already async. GetAwaiter().GetResult() is safe here because
        // the backing Cassandra driver never marshals back to the same synchronization
        // context that an ASP.NET request would occupy.
        try
        {
            var region = LookupAsync(raw, prefix).GetAwaiter().GetResult();
            if (TryParseRegion(raw, region, out var parsed))
            {
                StoreInCache(raw, parsed);
                return parsed;
            }
        }
        catch (Exception ex)
        {
            // Any exception (session not yet ready, network error) falls through to default.
            _logger.LogWarning(ex, "Region lookup for system {SystemId} failed; falling back to default region.", raw);
        }

        return CurrentRegion;
    }

    /// <summary>
    /// Asynchronous variant to be used in hot paths that already have an async context.
    /// Falls back to <see cref="CurrentRegion"/> when the registry row is absent.
    /// </summary>
    public async Task<ScyllaKeyspace> ResolveUserRegionAsync(
        string systemId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return CurrentRegion;

        var (raw, prefix) = SplitDiscriminatorPrefix(systemId);

        if (_cache.TryGetValue(raw, out var cached))
            return cached;

        var region = await LookupAsync(raw, prefix, cancellationToken);
        if (TryParseRegion(raw, region, out var parsed))
        {
            StoreInCache(raw, parsed);
            return parsed;
        }

        return CurrentRegion;
    }

    /// <summary>Populates the cache for a user whose home region is already known
    /// (e.g. after account registration).</summary>
    public void RegisterRegion(string systemId, ScyllaKeyspace region)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return;

        var (raw, _) = SplitDiscriminatorPrefix(systemId);
        StoreInCache(raw, region);
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private bool TryParseRegion(string systemId, string? raw, out ScyllaKeyspace region)
    {
        region = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        try
        {
            region = EnumWireExtensions.ParseScyllaKeyspace(raw);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            // A corrupt registry row must not poison the cache or crash the caller —
            // log loudly and let the caller fall back to the default region.
            _logger.LogWarning(ex,
                "Registry region '{Region}' for system {SystemId} is not a known keyspace; falling back to default region.",
                raw, systemId);
            return false;
        }
    }

    private async Task<string?> LookupAsync(
        string normalizedSystemId,
        string? prefix,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            SimpleStatement query;

            if (prefix == "username")
            {
                query = new SimpleStatement(
                    $"SELECT region FROM {ScyllaGlobalKeyspace.Name}.user_registry WHERE username = ? LIMIT 1",
                    normalizedSystemId);
            }
            else if (prefix == "discord")
            {
                query = new SimpleStatement(
                    $"SELECT region FROM {ScyllaGlobalKeyspace.Name}.user_registry WHERE discord_id = ? LIMIT 1",
                    normalizedSystemId);
            }
            else 
            {
                query = new SimpleStatement(
                    $"SELECT region FROM {ScyllaGlobalKeyspace.Name}.user_registry WHERE user_id = ? LIMIT 1",
                    normalizedSystemId);                
            }

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row?.GetValue<string>("region");
        }, _options, cancellationToken, _logger);
    }

    private void StoreInCache(string key, ScyllaKeyspace region)
    {
        if (_cache.Count >= MaxCacheSize)
        {
            // Simple eviction: clear on overflow to avoid unbounded growth.
            // A proper LRU would use a linked list; this is sufficient for phase-3 scope.
            _cache.Clear();
        }

        _cache[key] = region;
    }

    /// <summary>
    /// Split an incoming lookup id into (raw, prefix). Recognises the two shapes
    /// <c>ResolveUserRegion</c> sees: (a) region-scoped principal ids like <c>"nam:abcdefg"</c>
    /// where the prefix is a valid <see cref="ScyllaKeyspace"/>, and (b) discriminator-prefixed
    /// lookup keys like <c>"username:alice"</c> / <c>"discord:1234"</c> / <c>"id:abcdefg"</c>
    /// where the prefix names which registry column to query. Unscoped inputs return
    /// (systemId, null). This is the surviving prefix parser after Slice 4 collapsed the
    /// other two implementations — the "username" / "discord" branching would otherwise
    /// require reaching into ScopedSystemId internals for a case that isn't a scoped id.
    /// </summary>
    private static (string raw, string? prefix) SplitDiscriminatorPrefix(string systemId)
    {
        if (ScopedSystemId.TryParseScoped(systemId, out var scoped))
        {
            // Emit the canonical lowercase region tag so LookupAsync's prefix-equality checks
            // (== "username" / == "discord") see a stable byte shape.
            return (scoped.RawId, scoped.Region.ToWireValue());
        }

        var separator = systemId.IndexOf(':');
        if (separator > 0 && separator < systemId.Length - 1)
        {
            return (systemId[(separator + 1)..], systemId[..separator].ToLowerInvariant());
        }

        return (systemId, null);
    }
}
