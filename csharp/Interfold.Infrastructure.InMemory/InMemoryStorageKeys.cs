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
    public static string NormalizeSystemId(SystemId systemId) => NormalizeSystemId(systemId.Value);

    public static string NormalizeSystemId(string systemId)
        => SystemIdNormalization.StripRegionPrefix(systemId);

    /// <summary>The per-system dictionary partition key: <c>"{region}:{systemId}"</c>.</summary>
    public static string ForSystem(IRegionContext regionContext, SystemId systemId)
    {
        var region = regionContext.ResolveUserRegion(systemId).ToWireValue();
        return $"{region}:{systemId.Value}";
    }
}
