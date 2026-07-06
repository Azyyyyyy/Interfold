namespace Interfold.Contracts.Ids;

/// <summary>
/// The canonical region-prefix strip for system ids (<c>"nam:abcdefg"</c> → <c>"abcdefg"</c>).
/// Kept as the public shim over <see cref="ScopedSystemId.StripLeadingRegionPrefix"/> so the
/// three legacy call sites (Scylla keyspace resolver, InMemory storage keys, socket topic
/// comparison) do not have to reach into ScopedSystemId internals directly. Note this is
/// NOT <c>Interfold.Domain.FriendshipIdNormalization</c>, which deliberately re-applies the
/// principal's prefix — different semantics, kept separate.
///
/// <para>
/// <b>Semantics change (Slice 4).</b> The strip now fires only when the prefix is a
/// recognised region (<c>nam</c>, <c>eur</c>, <c>sam</c>, <c>sas</c>, <c>eas</c>,
/// <c>ocn</c>, <c>gdpr</c>). The three live call sites always deal with principal ids that
/// carry a valid region, so behaviour there is unchanged; the previous "strip any prefix
/// with a colon" behaviour would have silently corrupted a <c>username:alice</c> input,
/// which the new logic leaves untouched.
/// </para>
///
/// <para>
/// <b>Related Slice 7 helper.</b> Handles that carry a discriminator prefix
/// (<c>username:</c>, <c>discord:</c>, <c>id:</c>) are the domain of
/// <see cref="LookupHandle.TryParse"/>, not this method. Callers that need the routing
/// table (which registry column to hit for a given prefix) should use <c>LookupHandle</c>;
/// callers that just need to canonicalise a principal id keep using
/// <c>StripRegionPrefix</c>.
/// </para>
/// </summary>
public static class SystemIdNormalization
{
    /// <summary>Typed overload — strips from the wrapped raw value.</summary>
    public static string StripRegionPrefix(SystemId systemId) => StripRegionPrefix(systemId.Value);

    public static string StripRegionPrefix(string systemId)
        => ScopedSystemId.StripLeadingRegionPrefix(systemId);
}
