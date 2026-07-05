using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.Scylla;

public interface IScyllaKeyspaceResolver
{
    string DefaultKeyspace { get; }
    string ResolveRegionalKeyspace(string systemId);
    string ResolveGlobalKeyspace();
    string NormalizeSystemId(string systemId);
}

/// <summary>
/// Typed-id convenience overloads: repositories carry <see cref="Interfold.Contracts.Ids.SystemId"/>
/// end-to-end and unwrap to the raw string exactly once, here at the keyspace/normalization boundary.
/// </summary>
public static class ScyllaKeyspaceResolverExtensions
{
    public static string ResolveRegionalKeyspace(this IScyllaKeyspaceResolver resolver, Interfold.Contracts.Ids.SystemId systemId)
        => resolver.ResolveRegionalKeyspace(systemId.Value);

    public static string NormalizeSystemId(this IScyllaKeyspaceResolver resolver, Interfold.Contracts.Ids.SystemId systemId)
        => resolver.NormalizeSystemId(systemId.Value);

    /// <summary>
    /// Region-strip a <see cref="Interfold.Contracts.Ids.SystemId"/> and return a fresh
    /// <see cref="Interfold.Contracts.Ids.SystemId"/> so repository private helpers can
    /// carry the typed id through the operation instead of unwrapping to <see cref="string"/>
    /// at every hop. CQL binds still use <c>.Value</c> at the storage boundary.
    /// </summary>
    public static Interfold.Contracts.Ids.SystemId NormalizeTyped(this IScyllaKeyspaceResolver resolver, Interfold.Contracts.Ids.SystemId systemId)
        => new(resolver.NormalizeSystemId(systemId.Value));
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

    public string ResolveRegionalKeyspace(string systemId)
    {
        // Slice 4: TryParseScoped subsumes the "extract prefix + guard against id/username/discord"
        // dance the pre-Slice-4 code did by hand. A valid-region prefix routes straight to that
        // keyspace; every other shape (no prefix, id/username/discord discriminator, unknown
        // region tag) falls through to the region-context lookup — a strict improvement over the
        // old code, which returned "unknown" verbatim for an unrecognised prefix and would then
        // attempt CQL against a non-existent keyspace.
        if (Contracts.Ids.ScopedSystemId.TryParseScoped(systemId, out var scoped))
        {
            return scoped.Region.ToWireValue();
        }

        return _regionContext.ResolveUserRegion(systemId).ToWireValue();
    }

    public string ResolveGlobalKeyspace() => ScyllaGlobalKeyspace.Name;

    public string NormalizeSystemId(string systemId)
        => Contracts.Ids.SystemIdNormalization.StripRegionPrefix(systemId);
}
