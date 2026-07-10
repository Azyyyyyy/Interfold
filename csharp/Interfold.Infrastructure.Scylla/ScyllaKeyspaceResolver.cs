using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.Scylla;

/// <summary>
/// CQL-boundary resolver for regional and global keyspaces plus the region-strip normalisation
/// applied to every system id before it reaches a bind slot.
///
/// <para>
/// Step 2 of the strong-typing rescan retyped this interface so both id-taking members carry
/// <see cref="SystemId"/> directly (instead of raw <see cref="string"/>) and
/// <see cref="NormalizeSystemId"/> returns a <see cref="SystemId"/> too. Before this change,
/// callers split into two competing idioms:
/// <list type="bullet">
///   <item>Friendship / SettingsField used a <c>NormalizeTyped</c> extension that wrapped the
///     string result back into <see cref="SystemId"/> and threaded the typed value through the
///     rest of the operation, unwrapping via <c>.Value</c> at each CQL bind.</item>
///   <item>Journal / Tag / Poll / Fronting / Alter / ImportOperation kept a
///     <c>string normalizedSystemId</c> local and passed the bare string to ~18 CQL binds per
///     file, meaning any intermediate helper hop lost the type entirely.</item>
/// </list>
/// Retyping the interface itself makes the typed path the default; the CQL bind boundary is
/// still an unwrap (<c>normalizedSystemId.Value</c>) but every hop above the bind stays
/// <see cref="SystemId"/>-shaped.
/// </para>
///
/// <para>
/// The two members that still return / expose <see cref="string"/> —
/// <see cref="DefaultKeyspace"/>, <see cref="ResolveGlobalKeyspace"/>, and the return of
/// <see cref="ResolveRegionalKeyspace"/> — are the CQL wire boundary: keyspace names are
/// interpolated verbatim into CQL text via <c>$"{keyspace}.table"</c> and cannot be typed
/// further without introducing a wrapper whose only use is <c>.ToString()</c> at every site.
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

        // Slice 4: JWT-derived principals arrive scoped (nam:sys-abc) while public-route
        // bindings stay raw (sys-abc). The pre-fix TryParseScoped fast path routed scoped
        // ids straight to the prefix region and raw ids through user_registry — the same
        // principal could be written in nam.* and read from eur.* when the registry row
        // was absent or carried a different home region. Canonicalise to the stripped raw
        // id and resolve through IRegionContext so both wire forms share one keyspace.
        //
        // NormalizeSystemId now returns SystemId, so we can hand the typed value straight to
        // IRegionContext's SystemId overload without a string round-trip.
        return _regionContext.ResolveUserRegion(NormalizeSystemId(systemId)).ToWireValue();
    }

    public string ResolveGlobalKeyspace() => ScyllaGlobalKeyspace.Name;

    public SystemId NormalizeSystemId(SystemId systemId)
        => new(SystemIdNormalization.StripRegionPrefix(systemId.Value));
}
