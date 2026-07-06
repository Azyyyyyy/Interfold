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

        // Slice 7: LookupHandle.TryParse replaces the private SplitDiscriminatorPrefix from
        // Slice 4. A parseable handle carries an explicit Kind so LookupAsync knows which
        // registry column to hit; an unparseable input (unknown prefix, or bare-prefix like
        // "nam:") falls through to the "opaque bare id" branch inside LookupAsync with the
        // whole systemId as the query value — matching the strict-rejection contract in
        // LookupHandle.TryParse's xml-doc.
        var (cacheKey, handle) = HandleForLookup(systemId);

        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Synchronous path: attempt a best-effort lookup on the calling thread.
        // This keeps the interface non-async while avoiding a thread-pool deadlock on most
        // callers that are already async. GetAwaiter().GetResult() is safe here because
        // the backing Cassandra driver never marshals back to the same synchronization
        // context that an ASP.NET request would occupy.
        try
        {
            var region = LookupAsync(handle, systemId).GetAwaiter().GetResult();
            if (TryParseRegion(cacheKey, region, out var parsed))
            {
                StoreInCache(cacheKey, parsed);
                return parsed;
            }
        }
        catch (Exception ex)
        {
            // Any exception (session not yet ready, network error) falls through to default.
            _logger.LogWarning(ex, "Region lookup for system {SystemId} failed; falling back to default region.", cacheKey);
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

        var (cacheKey, handle) = HandleForLookup(systemId);

        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var region = await LookupAsync(handle, systemId, cancellationToken);
        if (TryParseRegion(cacheKey, region, out var parsed))
        {
            StoreInCache(cacheKey, parsed);
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

        var (cacheKey, _) = HandleForLookup(systemId);
        StoreInCache(cacheKey, region);
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
        LookupHandle? handle,
        string originalInput,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);

            // Fallback shape (handle == null) happens when LookupHandle.TryParse rejected
            // the input as unparseable — bare-prefix like "nam:", or an unknown non-region
            // prefix like "xxx:abcdefg". We deliberately query user_id with the WHOLE
            // original input so the strict-rejection contract holds: a rejected handle
            // becomes an opaque bare-id lookup, never a silent prefix-strip.
            var (column, value) = handle switch
            {
                { Kind: LookupKind.Username } h => ("username", h.RawId),
                { Kind: LookupKind.Discord } h => ("discord_id", h.RawId),
                { Kind: LookupKind.Region } h => ("user_id", h.RawId),
                { Kind: LookupKind.Id } h => ("user_id", h.RawId),
                _ => ("user_id", originalInput),
            };

            var query = new SimpleStatement(
                $"SELECT region FROM {ScyllaGlobalKeyspace.Name}.user_registry WHERE {column} = ? LIMIT 1",
                value);

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
    /// Turn an incoming lookup input into a (cache key, parsed handle) pair.
    /// <see cref="LookupHandle.TryParse"/> is the single source of truth for the routing
    /// table; this helper just picks the cache key so different-shape inputs referring to
    /// the same user (bare id, region-scoped id, explicit <c>id:</c> prefix) collapse
    /// onto the same cache entry while username / Discord lookups keep their own key
    /// space (there's no ambiguity — a username string can't collide with a system id in
    /// the same 7-char alphabet). Unparseable input keeps its whole original string as
    /// the cache key so a rejected handle round-trips deterministically.
    /// </summary>
    private static (string cacheKey, LookupHandle? handle) HandleForLookup(string systemId)
    {
        if (!LookupHandle.TryParse(systemId, out var parsed))
        {
            return (systemId, null);
        }

        var cacheKey = parsed.Kind switch
        {
            // System-id-shaped inputs canonicalise onto RawId so "abcdefg",
            // "nam:abcdefg", and "id:abcdefg" collide in the cache (they all identify the
            // same user_registry row).
            LookupKind.Region => parsed.RawId,
            LookupKind.Id => parsed.RawId,
            // Username / Discord handles keep their prefix in the cache key so a username
            // "abcdefg" doesn't spuriously alias the bare id "abcdefg".
            _ => parsed.OriginalValue,
        };

        return (cacheKey, parsed);
    }
}
