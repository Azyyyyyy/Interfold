using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.Scylla;

/// <summary>
/// CQL-boundary resolver for regional and global keyspaces plus the region-strip
/// normalisation applied to every system id before it reaches a bind slot.
/// <see cref="NormalizeSystemId"/> returns the underlying primitive <see cref="string"/>
/// so bind sites can pass the result directly to <c>SimpleStatement</c> — the DataStax
/// driver cannot serialize the <see cref="SystemId"/> wrapper struct, so unwrapping here
/// (rather than at each call site) prevents drift from re-introducing the "Unknown
/// Cassandra target type" runtime exception.
/// </summary>
public interface IScyllaKeyspaceResolver
{
    string DefaultKeyspace { get; }

    /// <summary>
    /// Resolve the regional keyspace that owns the given <paramref name="systemId"/>.
    /// Keyspace names are interpolated verbatim into CQL text at the call site.
    /// </summary>
    string ResolveRegionalKeyspace(SystemId systemId);

    string ResolveGlobalKeyspace();

    /// <summary>
    /// Region-strip a <see cref="SystemId"/> and return the raw underlying <see cref="string"/>
    /// suitable for direct CQL bind.
    /// </summary>
    string NormalizeSystemId(SystemId systemId);
}

public sealed class ScyllaKeyspaceResolver : IScyllaKeyspaceResolver
{
    private readonly IRegionContext _regionContext;

    public ScyllaKeyspaceResolver(IRegionContext regionContext)
    {
        _regionContext = regionContext;
    }

    // IRegionContext's resolution APIs are typed ScyllaKeyspace; this resolver is the CQL
    // boundary, so it unwraps to the lowercase keyspace name exactly once here.
    public string DefaultKeyspace => _regionContext.CurrentRegion.ToWireValue();

    public string ResolveRegionalKeyspace(SystemId systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId.Value))
            return DefaultKeyspace;

        // JWT-derived principals arrive scoped (nam:sys-abc) while public-route bindings
        // stay raw (sys-abc). Canonicalise to the stripped raw id and resolve through
        // IRegionContext so both wire forms share one keyspace — otherwise the same
        // principal could be written to nam.* and read from eur.*.
        return _regionContext.ResolveUserRegion(new SystemId(NormalizeSystemId(systemId))).ToWireValue();
    }

    public string ResolveGlobalKeyspace() => ScyllaGlobalKeyspace.Name;

    public string NormalizeSystemId(SystemId systemId)
        => SystemIdNormalization.StripRegionPrefix(systemId.Value);
}
