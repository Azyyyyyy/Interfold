using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Interfold.Contracts.Enums;

namespace Interfold.Contracts.Ids;

/// <summary>
/// A <see cref="SystemId"/> guaranteed to be in the scoped <c>{region}:{rawId}</c> wire form.
/// This is the compile-time enforcement layer for the invariant that pre-Slice-4 code only
/// ambiently maintained: every persistence-adapter partition key, every
/// <c>InProcessEventBus</c> routing key, every JWT <c>sub</c>, and every Postgres
/// idempotency PK sees a scoped composite (never a raw or double-prefixed id).
///
/// <para>
/// <b>Idempotent by construction.</b> Both
/// <see cref="Compose(ScyllaKeyspace, string)"/> and
/// <see cref="Compose(ScyllaKeyspace, SystemId)"/> strip any existing region prefix from the
/// input before re-applying the resolved region, so passing in an already-scoped string
/// produces the exact same <see cref="Value"/> as passing in the bare id. The
/// <c>"nam:nam:abcdefg"</c> footgun the pre-Slice-4 hand-formatted concatenations produce is
/// impossible to construct through this type.
/// </para>
///
/// <para>
/// <b>Wire compatibility.</b> <see cref="Value"/> is byte-identical to the scoped-form string
/// the pre-Slice-4 codebase emitted, and the JSON converter writes it verbatim (matches
/// <c>SystemIdJsonConverter</c>). Existing rows keyed by <c>PrincipalId.Value</c> in Postgres
/// / Scylla / InMemory remain readable without migration.
/// </para>
///
/// <para>
/// <b>Not to be confused with</b> <see cref="UsernameOrSystemId"/> (which expresses "either a
/// username OR a raw system id" at route-binding time) or
/// <c>Interfold.Domain.FriendshipIdNormalization</c> (which re-applies the principal's
/// prefix rather than stripping — different semantics).
/// </para>
/// </summary>
[JsonConverter(typeof(ScopedSystemIdJsonConverter))]
public readonly record struct ScopedSystemId : IParsable<ScopedSystemId>
{
    /// <summary>
    /// The canonical wire form, e.g. <c>"nam:abcdefg"</c>. This is what
    /// <see cref="SystemId.Value"/> returns for a principal today, and what every persistence
    /// adapter binds as its row PK. Never null; never empty for a well-constructed value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// The bare id with the region prefix stripped, e.g. <c>"abcdefg"</c>. Scylla partition
    /// keys and the friendship-normalization "reapply principal's region" path read this.
    /// </summary>
    public string RawId { get; }

    /// <summary>The home region — matches the prefix on <see cref="Value"/>.</summary>
    public ScyllaKeyspace Region { get; }

    private ScopedSystemId(string value, string rawId, ScyllaKeyspace region)
    {
        Value = value;
        RawId = rawId;
        Region = region;
    }

    /// <summary>
    /// Compose from an explicitly-resolved region and a possibly-scoped id string. Any
    /// existing region prefix on <paramref name="maybeScoped"/> is stripped first, so calling
    /// <see cref="Compose(ScyllaKeyspace, string)"/> with either <c>"abcdefg"</c> or
    /// <c>"nam:abcdefg"</c> yields the same result when the region argument is
    /// <see cref="ScyllaKeyspace.Nam"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="maybeScoped"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="maybeScoped"/> is blank after strip.</exception>
    public static ScopedSystemId Compose(ScyllaKeyspace region, string maybeScoped)
    {
        ArgumentNullException.ThrowIfNull(maybeScoped);
        var raw = StripLeadingRegionPrefix(maybeScoped);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("Cannot compose a ScopedSystemId from a blank id.", nameof(maybeScoped));
        }

        var regionWire = region.ToWireValue();
        return new ScopedSystemId($"{regionWire}:{raw}", raw, region);
    }

    /// <summary>
    /// Compose from an explicitly-resolved region and a typed <see cref="SystemId"/>. Any
    /// existing prefix on <paramref name="systemId"/> is stripped first — the composer is
    /// idempotent whether the caller already carried a scoped value or a raw one.
    /// </summary>
    public static ScopedSystemId Compose(ScyllaKeyspace region, SystemId systemId)
        => Compose(region, systemId.Value);

    /// <summary>
    /// Compose from a string-typed wire-form region tag (e.g. what
    /// <c>IScyllaKeyspaceResolver.ResolveRegionalKeyspace</c> returns) and a possibly-scoped
    /// id. Behaves identically to <see cref="Compose(ScyllaKeyspace, string)"/> once the
    /// region is parsed; unknown region strings throw with a clear error, matching the
    /// contract of the enum overload's fail-fast on invalid input.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="regionWire"/> is not a recognised region tag.</exception>
    public static ScopedSystemId Compose(string regionWire, string maybeScoped)
    {
        if (!TryParseRegion(regionWire, out var region))
        {
            throw new ArgumentException(
                $"'{regionWire}' is not a recognised region wire tag (expected one of nam, eur, sam, sas, eas, ocn, gdpr).",
                nameof(regionWire));
        }
        return Compose(region, maybeScoped);
    }

    /// <summary>
    /// Parse a wire-form string that is expected to already carry a valid region prefix.
    /// Throws for unscoped, blank, or unknown-region input — the strict-parse path is the
    /// right choice at trust boundaries (JWT <c>sub</c>, incoming events) where a caller
    /// that lost the prefix indicates a real bug rather than a legacy shape to be tolerated.
    /// </summary>
    /// <exception cref="ArgumentException">Input is unscoped, blank, or the region tag is unknown.</exception>
    public static ScopedSystemId ParseScoped(string wire)
    {
        if (!TryParseScoped(wire, out var result))
        {
            throw new ArgumentException(
                $"'{wire}' is not a valid scoped system id (expected '<region>:<rawId>' with region in {{nam, eur, sam, sas, eas, ocn, gdpr}}).",
                nameof(wire));
        }
        return result;
    }

    /// <summary>
    /// Attempt to parse a wire-form string. Returns <c>false</c> (never throws) for null,
    /// empty, whitespace-only, unscoped, or unknown-region input. Used by every read-side
    /// site that needs to tolerate legacy shapes (route binding, InterfoldPrincipalMiddleware
    /// on legacy JWTs, the region-context lookup cache).
    /// </summary>
    public static bool TryParseScoped([NotNullWhen(true)] string? wire, out ScopedSystemId result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(wire))
        {
            return false;
        }

        var separator = wire.IndexOf(':');
        if (separator <= 0 || separator >= wire.Length - 1)
        {
            return false;
        }

        var regionRaw = wire[..separator];
        var raw = wire[(separator + 1)..];
        if (!TryParseRegion(regionRaw, out var region))
        {
            return false;
        }

        // Canonicalise the prefix casing on the way out — the ScyllaKeyspace wire values are
        // always lowercase, and the six read-side call sites (SystemTopic, ScyllaKeyspaceResolver,
        // ScyllaUserRegistryRegionContext) all compare against the lowercase form.
        result = new ScopedSystemId($"{region.ToWireValue()}:{raw}", raw, region);
        return true;
    }

    /// <summary>Return this scoped id as an unmarked <see cref="SystemId"/> for wire-boundary consumers.</summary>
    public SystemId AsSystemId() => new(Value);

    /// <summary>
    /// Semantic "does <paramref name="candidate"/> refer to the same user as this principal?"
    /// This is the correct primitive for controllers guarding against self-request /
    /// self-friendship / self-trust. The obvious <c>principal == candidate</c> spelling
    /// (via the implicit <see cref="ScopedSystemId"/>→<see cref="SystemId"/> widen) is a
    /// byte compare of the scoped composite against whatever the client sent in the route
    /// segment: it catches the trivial <c>"nam:abcdefg"</c> shape but silently misses the
    /// raw (<c>"abcdefg"</c>) shape because the strings differ. This method canonicalises
    /// <paramref name="candidate"/> before comparing so raw and same-region-scoped inputs
    /// both self-reject.
    ///
    /// <para>
    /// <b>Cross-region shapes.</b> If <paramref name="candidate"/> carries its own region
    /// prefix (e.g. <c>"eur:abcdefg"</c>) it is compared byte-for-byte against the scoped
    /// composite — a cross-region id that happens to share this raw id is treated as a
    /// different user, matching the "scoped composite is identity" contract in the type-level
    /// xml-doc above.
    /// </para>
    ///
    /// <para>
    /// <b>Not to be confused with</b> <see cref="Compose(ScyllaKeyspace, SystemId)"/> which
    /// unconditionally re-applies this region — that shape is right for the
    /// <c>FriendshipIdNormalization.CanonicalizeForPrincipal</c> callers that WANT
    /// principal-region coercion, and wrong for the controller self-check where we do not
    /// want the client's <c>eur:abcdefg</c> route to coerce into <c>nam:abcdefg</c> and
    /// falsely self-reject.
    /// </para>
    /// </summary>
    public bool RepresentsSameUserAs(SystemId candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Value))
        {
            return false;
        }

        // If the candidate carries its own region prefix, compare scoped-to-scoped.
        // The strict parse returns false for anything without a valid region tag, so a
        // discriminator-prefixed input like "username:alice" (which should never reach
        // this overload since it belongs to UsernameOrSystemId) also falls through to
        // the raw-id branch and yields "not self" because "alice" != this.RawId.
        if (TryParseScoped(candidate.Value, out var scopedCandidate))
        {
            return string.Equals(Value, scopedCandidate.Value, StringComparison.Ordinal);
        }

        // Bare / raw shape → treat as living in this principal's region. The pre-Round-2
        // byte-level `principal == candidate` spelling silently missed this case; every
        // /api/friends/{rawId} and /api/friend-requests/{rawId}/{action} route hit here.
        return string.Equals(RawId, candidate.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Route-binding overload for the friend-request send path where the route segment is
    /// bound as <see cref="UsernameOrSystemId"/>. Delegates to the
    /// <see cref="RepresentsSameUserAs(SystemId)"/> primitive when the candidate parses as
    /// a system-id shape (bare or scoped or explicit <c>id:</c> prefix). Username and
    /// Discord shapes always return <see langword="false"/> — those require a registry
    /// lookup to resolve to a concrete system id, so the fast-path can't decide self here
    /// and the downstream handler's post-resolution self-check takes over (see
    /// <c>SendFriendRequestCommandHandler</c>'s resolved-id self-guard). This preserves
    /// the Slice-7 fast-path semantics — the controller returns the crisp
    /// <c>cannot_send_self</c> error for the trivial "client sends their own id" case
    /// without a repository hop — while replacing the fragile raw string compare that
    /// silently coupled the two wrapper types' internal <c>.Value</c> shapes.
    /// </summary>
    public bool RepresentsSameUserAs(UsernameOrSystemId candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Value))
        {
            return false;
        }

        if (!LookupHandle.TryParse(candidate.Value, out var handle))
        {
            // Not a parseable handle shape (e.g. "xxx:abcdefg") → fall back to the raw
            // byte-level compare so an opaque non-handle input still catches the trivial
            // case where the client happened to send this principal's exact scoped
            // composite as the route segment. The downstream resolver rejects the opaque
            // input separately.
            return string.Equals(Value, candidate.Value, StringComparison.Ordinal);
        }

        return handle.Kind switch
        {
            // Region-scoped or explicit id: shapes translate directly into a SystemId
            // that the primitive can canonicalise. Bare 7-char handles parse as
            // LookupKind.Id with RawId == input, so the primitive's raw-id branch fires.
            LookupKind.Region => RepresentsSameUserAs(new SystemId(candidate.Value)),
            LookupKind.Id => RepresentsSameUserAs(new SystemId(handle.RawId)),

            // Username / Discord shapes require a registry lookup — the fast-path can't
            // decide self here without hitting the repository. Fall through to the
            // downstream handler's post-resolution self-check.
            _ => false,
        };
    }

    /// <summary>
    /// Implicit widen to <see cref="SystemId"/>. Every persistence adapter, event constructor,
    /// and repository entry point downstream of the middleware still declares
    /// <see cref="SystemId"/> parameters (the "unmarked scoped composite" shape) — Slice 4
    /// tightens the type at ingress (<c>CommandEnvelope.PrincipalId</c>,
    /// <c>ITargetedClusterEvent.TargetSystemId</c>) without forcing the downstream signatures
    /// to churn. The widen is byte-identical (<see cref="Value"/> is what
    /// <see cref="SystemId.Value"/> returns for a principal today), so it does not alter any
    /// wire representation.
    /// </summary>
    public static implicit operator SystemId(ScopedSystemId scoped) => scoped.AsSystemId();

    public override string ToString() => Value;

    // --- IParsable — enables [FromRoute] ScopedSystemId systemId binding on controllers. ---
    // Route-bound values come from client-controlled URLs, so we use the strict-parse path:
    // an unscoped path segment surfaces as a 400 instead of silently defaulting.

    public static ScopedSystemId Parse(string s, IFormatProvider? provider) => ParseScoped(s);

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out ScopedSystemId result)
        => TryParseScoped(s, out result);

    // --- Internal helpers (also used by SystemIdNormalization to keep one source of truth) ---

    /// <summary>
    /// Strip a leading <c>{region}:</c> prefix if present. The strip fires only when the
    /// substring before the first colon is a recognised region tag; every other input
    /// (blank, no colon, colon-at-start, non-region prefix like <c>"username:"</c>) is
    /// returned unchanged. Refusing to strip unknown prefixes preserves the strict
    /// routing contract in <see cref="LookupHandle.TryParse"/> — post-Slice-7 the
    /// discriminator prefixes (<c>username:</c> / <c>discord:</c> / <c>id:</c>) are the
    /// domain of <c>LookupHandle</c>, and this method's job is unchanged: strip a region
    /// tag or leave the input alone. A bare prefix like <c>"nam:"</c> deliberately returns
    /// empty so <see cref="Compose(ScyllaKeyspace, string)"/> surfaces the caller-bug via
    /// its blank-raw guard rather than emitting a malformed <c>"nam:nam:"</c>.
    /// </summary>
    internal static string StripLeadingRegionPrefix(string maybeScoped)
    {
        if (string.IsNullOrWhiteSpace(maybeScoped))
        {
            return maybeScoped;
        }

        var separator = maybeScoped.IndexOf(':');
        if (separator <= 0)
        {
            return maybeScoped;
        }

        if (!TryParseRegion(maybeScoped[..separator], out _))
        {
            return maybeScoped;
        }

        return maybeScoped[(separator + 1)..];
    }

    /// <summary>
    /// Case-insensitive region-tag lookup that never throws. Mirrors
    /// <see cref="EnumWireExtensions.ParseScyllaKeyspace"/> without the "blank defaults to Nam"
    /// fallback — a blank prefix here is unambiguously "no prefix at all".
    /// </summary>
    internal static bool TryParseRegion(string raw, out ScyllaKeyspace region)
    {
        region = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "nam": region = ScyllaKeyspace.Nam; return true;
            case "eur": region = ScyllaKeyspace.Eur; return true;
            case "sam": region = ScyllaKeyspace.Sam; return true;
            case "sas": region = ScyllaKeyspace.Sas; return true;
            case "eas": region = ScyllaKeyspace.Eas; return true;
            case "ocn": region = ScyllaKeyspace.Ocn; return true;
            case "gdpr": region = ScyllaKeyspace.Gdpr; return true;
            default: return false;
        }
    }
}

/// <summary>
/// Emits and reads the raw <see cref="ScopedSystemId.Value"/> string — byte-identical to
/// <c>SystemIdJsonConverter</c> so migrating a field's declared type from
/// <see cref="SystemId"/> to <see cref="ScopedSystemId"/> produces zero wire change.
/// </summary>
internal sealed class ScopedSystemIdJsonConverter : JsonConverter<ScopedSystemId>
{
    public override ScopedSystemId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        // The strict parse is deliberate here: inbound JSON that lost the scope during
        // hand-crafting a fixture or during a bad publisher would otherwise silently
        // deserialise to an invalid ScopedSystemId. Fail-fast on the first bad byte.
        return ScopedSystemId.ParseScoped(raw ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, ScopedSystemId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
