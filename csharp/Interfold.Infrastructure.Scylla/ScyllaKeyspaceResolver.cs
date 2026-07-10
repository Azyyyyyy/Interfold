using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.Scylla;

/// <summary>
/// CQL-boundary resolver for regional and global keyspaces plus the region-strip
/// normalisation applied to every system id before it reaches a bind slot. Both id-taking
/// members take <see cref="SystemId"/> directly and <see cref="NormalizeSystemId"/>
/// returns a <see cref="SystemId"/>, so every hop above the CQL bind stays typed and only
/// the bind itself unwraps via <c>.Value</c>.
///
/// <para>
/// The keyspace-name members (<see cref="DefaultKeyspace"/>,
/// <see cref="ResolveGlobalKeyspace"/>, and the return of
/// <see cref="ResolveRegionalKeyspace"/>) stay <see cref="string"/> because keyspace
/// names are interpolated verbatim into CQL text via <c>$"{keyspace}.table"</c>; wrapping
/// them would only introduce a type whose sole use is <c>.ToString()</c> at every site.
/// </para>
/// </summary>
public interface IScyllaKeyspaceResolver
{
    string DefaultKeyspace { get; }

    /// <summary>
    /// Resolve the regional keyspace that owns the given <paramref name="systemId"/>. The
    /// return remains <see cref="string"/> because keyspace names are interpolated verbatim
    /// into CQL statements at the call site.
    /// </summary>
    string ResolveRegionalKeyspace(SystemId systemId);

    string ResolveGlobalKeyspace();

    /// <summary>
    /// Region-strip a <see cref="SystemId"/> and return the resulting <see cref="SystemId"/>
    /// so repository private helpers can carry the typed id through the operation and only
    /// unwrap to <see cref="string"/> at each CQL bind slot.
    /// </summary>
    SystemId NormalizeSystemId(SystemId systemId);
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
        return _regionContext.ResolveUserRegion(NormalizeSystemId(systemId)).ToWireValue();
    }

    public string ResolveGlobalKeyspace() => ScyllaGlobalKeyspace.Name;

    public SystemId NormalizeSystemId(SystemId systemId)
        => new(SystemIdNormalization.StripRegionPrefix(systemId.Value));
}
