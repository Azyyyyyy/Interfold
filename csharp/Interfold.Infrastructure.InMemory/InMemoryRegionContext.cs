using Interfold.Contracts.Enums;
using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.InMemory;

public sealed class InMemoryRegionContext : IRegionContext
{
    // Derived from the enum so the in-memory hash routing covers every region.
    private static readonly ScyllaKeyspace[] Regions = Enum.GetValues<ScyllaKeyspace>();

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

}
