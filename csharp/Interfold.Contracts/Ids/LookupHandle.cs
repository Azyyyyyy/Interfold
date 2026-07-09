using System.Diagnostics.CodeAnalysis;

namespace Interfold.Contracts.Ids;

/// <summary>
/// Which registry column a raw client-supplied handle should route to. The wire prefixes
/// map 1:1 onto this enum via <see cref="LookupHandle.TryParse"/> and every read-side
/// dispatch site switches on this value instead of comparing prefix strings.
/// </summary>
public enum LookupKind
{
    /// <summary>Region-scoped principal id, e.g. <c>"nam:abcdefg"</c>.</summary>
    Region,

    /// <summary>Username lookup, e.g. <c>"username:alice"</c>.</summary>
    Username,

    /// <summary>Discord snowflake lookup, e.g. <c>"discord:1234567"</c>.</summary>
    Discord,

    /// <summary>
    /// Bare 7-char system id or explicit <c>"id:"</c> prefix; routed to
    /// <c>user_registry.user_id</c>. The two shapes intentionally converge on the same
    /// registry column — <c>Kind.Id</c> is the strict "this string is a system id, not a
    /// username or a Discord snowflake" assertion.
    /// </summary>
    Id,
}

/// <summary>
/// A pre-resolution handle to a user, parsed from a raw client-supplied string. Distinct
/// from <see cref="ScopedSystemId"/> (which is a post-resolution principal id) and from
/// <see cref="UsernameOrSystemId"/> (which is the route-binding type before the repository
/// re-parses via <see cref="TryParse"/>).
///
/// <para>
/// The two current consumer sites are the read-side region-context reader
/// (<c>ScyllaUserRegistryRegionContext.LookupAsync</c>, which routes to the right
/// <c>user_registry</c> column) and the friendship resolve path
/// (<c>ScyllaFriendshipRepository.ResolveUserIdInScyllaAsync</c> and
/// <c>InMemoryFriendshipRepository.ResolveUserIdAsync</c>, which dispatch to
/// username-fanout / Discord-account-repo / user_registry accordingly).
/// </para>
///
/// <para>
/// <b>Strict rejection of unknown non-region prefixes.</b> An input like <c>"xxx:abcdefg"</c>
/// (where <c>xxx</c> is neither a region tag nor one of the four discriminator prefixes)
/// returns <see langword="false"/> from <see cref="TryParse"/>. Callers with a real
/// registry (Scylla: <c>user_registry.user_id</c>) are expected to treat "not parseable"
/// as "opaque bare id" and query the registry with the <b>whole</b> input (not the
/// after-colon slice) — the miss then surfaces as <c>friend_request:no_user</c> (422).
/// This is a behaviour change from the pre-Slice-7 <c>SplitDiscriminatorPrefix</c> which
/// silently stripped any prefix-with-colon.
/// </para>
///
/// <para>
/// <b>Backend asymmetry on the unparseable branch.</b> The "opaque round-trip → miss"
/// chain above works on Scylla / Cassandra because those backends have a real
/// <c>user_registry.user_id</c> lookup that produces the miss. The InMemory backend has
/// no such intermediate lookup: round-tripping an unparseable input would flow the
/// opaque id straight into the friend-request writer with no existence check, creating
/// a phantom friend request. So the InMemory dispatch returns <see langword="null"/>
/// directly for the unparseable branch instead of round-tripping — the observable
/// outcome (422 <c>friend_request:no_user</c>) is the same across all three backends
/// even though the InMemory path is shorter. See
/// <c>InMemoryFriendshipRepository.ResolveUserIdAsync</c>'s rationale block for the
/// full asymmetry and the cross-backend integration pin.
/// </para>
/// </summary>
public readonly record struct LookupHandle
{
    /// <summary>Which registry column the handle routes to.</summary>
    public LookupKind Kind { get; }

    /// <summary>
    /// The after-prefix content used as the actual lookup value. For bare inputs
    /// (<see cref="LookupKind.Id"/> without an explicit prefix) this is the whole input;
    /// for prefixed inputs it is the substring after the first colon.
    /// </summary>
    public string RawId { get; }

    /// <summary>The original untouched input string, retained for logging / audit.</summary>
    public string OriginalValue { get; }

    private LookupHandle(LookupKind kind, string rawId, string originalValue)
    {
        Kind = kind;
        RawId = rawId;
        OriginalValue = originalValue;
    }

    /// <summary>
    /// Parse a raw handle string. Returns <see langword="false"/> (never throws) for
    /// null / blank / bare-prefix (<c>"nam:"</c>) / unknown-prefix (<c>"xxx:abcdefg"</c>)
    /// inputs. See the type-level xml-doc for the full behaviour matrix.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? input, out LookupHandle handle)
    {
        handle = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var separator = input.IndexOf(':');
        if (separator < 0)
        {
            // No colon → bare 7-char system id shape.
            handle = new LookupHandle(LookupKind.Id, input, input);
            return true;
        }

        // Colon present but nothing before or after → not a valid prefixed handle. We
        // reject rather than fall back so the caller sees the input for what it is (a
        // client bug) rather than silently treating ":foo" as bare id "foo".
        if (separator == 0 || separator >= input.Length - 1)
        {
            return false;
        }

        var prefix = input[..separator];
        var afterColon = input[(separator + 1)..];

        // Region prefixes take priority — they're the canonical scoped-id shape and
        // ScopedSystemId's parser already treats these seven strings as region tags.
        if (ScopedSystemId.TryParseRegion(prefix, out _))
        {
            handle = new LookupHandle(LookupKind.Region, afterColon, input);
            return true;
        }

        // Discriminator prefixes are lowercase-canonical on the wire; normalise for a
        // case-insensitive client while still rejecting anything not on the allow-list.
        switch (prefix.ToLowerInvariant())
        {
            case "id":
                handle = new LookupHandle(LookupKind.Id, afterColon, input);
                return true;
            case "username":
                handle = new LookupHandle(LookupKind.Username, afterColon, input);
                return true;
            case "discord":
                handle = new LookupHandle(LookupKind.Discord, afterColon, input);
                return true;
            default:
                // Unknown non-region prefix. Return false so callers know to fall back to
                // the "opaque bare id" path with the WHOLE input, matching the
                // strict-rejection contract described in the type xml-doc.
                return false;
        }
    }
}
