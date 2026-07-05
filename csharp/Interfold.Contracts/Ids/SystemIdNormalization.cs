namespace Interfold.Contracts.Ids;

/// <summary>
/// The canonical region-prefix strip for system ids (<c>"nam:abcdefg"</c> → <c>"abcdefg"</c>).
/// One implementation for the three call sites that previously carried byte-identical
/// copies (Scylla keyspace resolver, InMemory storage keys, socket topic comparison).
/// Note this is NOT <c>Interfold.Domain.FriendshipIdNormalization</c>, which deliberately
/// re-applies the principal's prefix — different semantics, kept separate.
/// </summary>
public static class SystemIdNormalization
{
    /// <summary>Typed overload — strips from the wrapped raw value.</summary>
    public static string StripRegionPrefix(SystemId systemId) => StripRegionPrefix(systemId.Value);

    public static string StripRegionPrefix(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return systemId;
        }

        var separator = systemId.IndexOf(':');
        if (separator <= 0 || separator >= systemId.Length - 1)
        {
            return systemId;
        }

        return systemId[(separator + 1)..];
    }
}
