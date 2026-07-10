namespace Interfold.Contracts.Ids;

/// <summary>
/// The canonical region-prefix strip for system ids (<c>"nam:abcdefg"</c> → <c>"abcdefg"</c>).
/// Public shim over <see cref="ScopedSystemId.StripLeadingRegionPrefix"/> so callers
/// (Scylla keyspace resolver, InMemory storage keys, socket topic comparison) don't reach
/// into <c>ScopedSystemId</c> internals directly. Not to be confused with
/// <c>Interfold.Domain.FriendshipIdNormalization</c>, which deliberately re-applies the
/// principal's prefix — different semantics, kept separate.
///
/// <para>
/// The strip fires only when the prefix is a recognised region (<c>nam</c>, <c>eur</c>,
/// <c>sam</c>, <c>sas</c>, <c>eas</c>, <c>ocn</c>, <c>gdpr</c>). A non-region input like
/// <c>username:alice</c> is left untouched.
/// </para>
///
/// <para>
/// Handles that carry a discriminator prefix (<c>username:</c>, <c>discord:</c>,
/// <c>id:</c>) are the domain of <see cref="LookupHandle.TryParse"/>, not this method.
/// Callers that need the routing table (which registry column to hit for a given prefix)
/// should use <c>LookupHandle</c>; callers that just need to canonicalise a principal id
/// keep using <c>StripRegionPrefix</c>.
/// </para>
/// </summary>
public static class SystemIdNormalization
{
    /// <summary>Typed overload — strips from the wrapped raw value.</summary>
    public static string StripRegionPrefix(SystemId systemId) => StripRegionPrefix(systemId.Value);

    public static string StripRegionPrefix(string systemId)
        => ScopedSystemId.StripLeadingRegionPrefix(systemId);
}
