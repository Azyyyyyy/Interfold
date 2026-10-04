using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Abstractions;

/// <summary>Region resolution for the multi-region topology. Typed as
/// <see cref="ScyllaKeyspace"/>; region-prefix stripping lives on
/// <see cref="ScopedSystemId.StripRegionPrefix(SystemId)"/>.</summary>
public interface IRegionContext
{
    ScyllaKeyspace CurrentRegion { get; }

    ScyllaKeyspace ResolveUserRegion(SystemId systemId);
}
