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
        var explicitRegion = ExtractRegionPrefix(systemId);
        if (explicitRegion is not null and not ("id" or "username" or "discord"))
        {
            return explicitRegion;
        }

        return _regionContext.ResolveUserRegion(systemId).ToWireValue();
    }

    public string ResolveGlobalKeyspace() => "global";

    public string NormalizeSystemId(string systemId)
        => Contracts.Ids.SystemIdNormalization.StripRegionPrefix(systemId);

    private static string? ExtractRegionPrefix(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return null;
        }

        var separator = systemId.IndexOf(':');
        if (separator <= 0)
        {
            return null;
        }

        return systemId[..separator].ToLowerInvariant();
    }
}
