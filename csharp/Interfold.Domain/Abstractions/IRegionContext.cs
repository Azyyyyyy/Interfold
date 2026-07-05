using Interfold.Contracts.Enums;

namespace Interfold.Domain.Abstractions;

/// <summary>
/// Region resolution for the multi-region persistence topology. The resolution APIs are
/// typed as <see cref="ScyllaKeyspace"/>; callers unwrap to the lowercase wire name (via
/// <c>ToWireValue()</c>) only at CQL keyspace interpolation and system-key/prefix
/// construction. The <c>nam:</c> prefix inside <c>SystemId.Value</c> stays a string concern.
/// </summary>
public interface IRegionContext
{
    ScyllaKeyspace CurrentRegion { get; }
    ScyllaKeyspace ResolveUserRegion(string systemId);
}
