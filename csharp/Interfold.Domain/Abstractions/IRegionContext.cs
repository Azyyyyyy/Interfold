using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Abstractions;

/// <summary>
/// Region resolution for the multi-region persistence topology. Resolution is typed as
/// <see cref="ScyllaKeyspace"/>; callers unwrap to the lowercase wire name (via
/// <c>ToWireValue()</c>) only at CQL keyspace interpolation and system-key/prefix
/// construction. The <c>nam:</c> prefix inside <see cref="SystemId.Value"/> stays a string
/// concern owned by <see cref="ScopedSystemId"/> and <see cref="SystemIdNormalization"/>.
///
/// <para>
/// Step 3 of the strong-typing rescan removed the historical <c>ResolveUserRegion(string)</c>
/// overload. Every production caller reached this interface with a <see cref="SystemId"/>
/// in hand after Step 2 retyped <c>IScyllaKeyspaceResolver</c>; the string overload was
/// only kept alive by test call sites that hand-wrote raw ids for convenience. Tests now
/// wrap those raws in <see cref="SystemId"/> at the call site, mirroring how production
/// ingress (JWT middleware, route binding) constructs <see cref="SystemId"/> from wire
/// input at the trust boundary.
/// </para>
/// </summary>
public interface IRegionContext
{
    ScyllaKeyspace CurrentRegion { get; }

    ScyllaKeyspace ResolveUserRegion(SystemId systemId);
}
