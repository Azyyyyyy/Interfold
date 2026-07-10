using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.InMemory;

/// <summary>
/// Shared key/normalization helpers for the InMemory repositories, replacing the
/// per-repository copies. The formats are frozen: normalization strips a legacy
/// <c>"region:"</c> prefix (mirroring <c>ScyllaKeyspaceResolver.NormalizeSystemId</c>) and
/// system partition keys are <c>"{region}:{systemId}"</c>.
/// </summary>
internal static class InMemoryStorageKeys
{
    /// <summary>
    /// Region-strip a <see cref="SystemId"/> and return a fresh <see cref="SystemId"/> so the
    /// typed key can be used directly as a dictionary key without unwrapping to
    /// <see cref="string"/>. Preferred entry point for the InMemory repositories that key
    /// by a normalized system id (friendship, encryption, notification, auth-revocation, ...).
    /// </summary>
    public static SystemId Normalize(SystemId systemId)
        => new(SystemIdNormalization.StripRegionPrefix(systemId.Value));

    public static string NormalizeSystemId(SystemId systemId) => NormalizeSystemId(systemId.Value);

    public static string NormalizeSystemId(string systemId)
        => SystemIdNormalization.StripRegionPrefix(systemId);

    /// <summary>
    /// The per-system dictionary partition key: <c>"{region}:{systemId}"</c>. Routed through
    /// <see cref="ScopedSystemId.Compose(ScyllaKeyspace, SystemId)"/> so the composition is
    /// idempotent — an incoming <see cref="SystemId"/> that already carries a region prefix
    /// no longer produces a double-prefixed key like <c>"nam:nam:abcdefg"</c>, which was the
    /// silent failure mode of the pre-Slice-4 hand-concatenation.
    ///
    /// <para>
    /// Round-3 Commit 1 (canvas #1): returns the typed <see cref="ScopedSystemId"/> directly
    /// so the seven InMemory repos can key their per-system dictionaries on the wrapper
    /// (which is a <c>readonly record struct</c> with ordinal equality on its underlying
    /// string — behaviourally identical to the pre-Round-3 raw-string key). Round-2 Commit
    /// 12 retyped the dictionary <b>values</b> to speak wrappers but left the keys as raw
    /// string because retyping this funnel was cross-cutting; this finishes that story.
    /// </para>
    /// </summary>
    public static ScopedSystemId ForSystem(IRegionContext regionContext, SystemId systemId)
    {
        var region = regionContext.ResolveUserRegion(systemId);
        return ScopedSystemId.Compose(region, systemId);
    }
}
