using Interfold.Contracts.Enums;
using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.InMemory;

public sealed class InMemoryRegionContext : IRegionContext
{
    private static readonly ScyllaKeyspace[] Regions =
    [
        ScyllaKeyspace.Nam, ScyllaKeyspace.Eur, ScyllaKeyspace.Ocn,
        ScyllaKeyspace.Sam, ScyllaKeyspace.Sas, ScyllaKeyspace.Gdpr,
    ];

    public ScyllaKeyspace CurrentRegion { get; }

    public InMemoryRegionContext(ScyllaKeyspace currentRegion = ScyllaKeyspace.Nam)
    {
        CurrentRegion = currentRegion;
    }

    public ScyllaKeyspace ResolveUserRegion(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return CurrentRegion;
        }

        var index = Math.Abs(systemId.GetHashCode(StringComparison.Ordinal)) % Regions.Length;
        return Regions[index];
    }

    public string ResolveConsistency(ScyllaKeyspace targetRegion) =>
        targetRegion == CurrentRegion ? "local" : "global";
}
