using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

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

    /// <summary>
    /// Raw-string overload for legacy-prefixed spellings (<c>"nam:abcdefg"</c>) and
    /// pre-<see cref="SystemId"/> plumbing; prefer the typed overload where a
    /// <see cref="SystemId"/> is in hand.
    /// </summary>
    ScyllaKeyspace ResolveUserRegion(string systemId);

    ScyllaKeyspace ResolveUserRegion(SystemId systemId);
}
