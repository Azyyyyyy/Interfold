using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.Ids;

/// <summary>
/// Pins the <see cref="ScopedSystemId.RepresentsSameUserAs(SystemId)"/> and
/// <see cref="ScopedSystemId.RepresentsSameUserAs(UsernameOrSystemId)"/> primitives that
/// back the eight controller self-request guards (FriendRequestsController: Send / Cancel /
/// Accept / Reject; FriendsController: Show / Delete / Trust / Untrust).
///
/// <para>
/// The pre-Round-2 spelling was either <c>principal == id</c> (SystemId sites — a byte
/// compare of the scoped composite against the raw route value via the implicit
/// <see cref="ScopedSystemId"/>→<see cref="SystemId"/> widen) or
/// <c>principal.Value == id.Value</c> (Send — same byte compare, cross-wrapper). Both
/// silently missed the raw route shape (<c>/api/friends/{rawId}</c>), returning a
/// generic downstream error instead of the crisp <c>cannot_*_self</c>. These tests pin
/// the semantic-check invariants: same-region raw and same-region scoped both self-reject;
/// cross-region scoped is treated as a different user (matches the "scoped composite is
/// identity" contract on <see cref="ScopedSystemId"/>); blank / mismatched-raw / username
/// / Discord shapes never falsely self-reject.
/// </para>
/// </summary>
public sealed class ScopedSystemIdRepresentsSameUserAsTests
{
    private static readonly ScopedSystemId Principal = ScopedSystemId.Compose(ScyllaKeyspace.Nam, "abcdefg");

    // ---------------- SystemId overload: happy paths ----------------

    /// <summary>
    /// The scoped-composite route shape (<c>/api/friends/nam:abcdefg</c>) is the pre-Round-2
    /// only-case that the byte compare caught. RepresentsSameUserAs must still catch it to
    /// keep byte-compat for clients that already send scoped ids.
    /// </summary>
    [Test]
    public async Task SystemId_SameRegionScoped_IsSelf()
    {
        var candidate = new SystemId("nam:abcdefg");

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsTrue()
            .Because("A scoped candidate in the principal's region and with the principal's raw id is the same user — this is the case the pre-Round-2 byte compare already caught.");
    }

    /// <summary>
    /// The raw-id route shape (<c>/api/friends/abcdefg</c>) is the pre-Round-2 silent-miss.
    /// The pre-Round-2 spelling would have compared <c>"nam:abcdefg" == "abcdefg"</c> → false
    /// and fallen through to the downstream handler with a generic error. This test pins the
    /// fix that promotes the guard to the semantic level.
    /// </summary>
    [Test]
    public async Task SystemId_RawId_MatchingPrincipalRawId_IsSelf()
    {
        var candidate = new SystemId("abcdefg");

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsTrue()
            .Because("A raw candidate whose value equals the principal's RawId represents the same user in the principal's region; the pre-Round-2 byte compare silently missed this shape.");
    }

    /// <summary>
    /// Cross-region scoped candidates are treated as a different user, matching the
    /// "scoped composite is identity" contract on the type-level xml-doc. Concretely: a
    /// <c>eur:abcdefg</c> route from a <c>nam:abcdefg</c> principal is either a client-side
    /// bug or a genuinely different user (per the design where regions partition the
    /// user-id space). Either way, we do NOT want the self-guard to coerce it into the
    /// principal's region and falsely reject.
    /// </summary>
    [Test]
    public async Task SystemId_CrossRegionScopedWithSameRawId_IsNotSelf()
    {
        var candidate = new SystemId("eur:abcdefg");

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsFalse()
            .Because("A cross-region scoped id with the same raw id is a different user per the 'scoped composite is identity' contract — the self-guard must not coerce it into the principal's region.");
    }

    /// <summary>
    /// Different raw id in the same region is a different user. Sanity check on the
    /// primitive that would otherwise be indistinguishable from a broken always-true.
    /// </summary>
    [Test]
    public async Task SystemId_SameRegionDifferentRawId_IsNotSelf()
    {
        var candidate = new SystemId("nam:xyzzyxq");

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsFalse()
            .Because("Same-region scoped candidates with a different raw id are different users.");
    }

    /// <summary>
    /// Blank / whitespace inputs return false rather than throwing. Route binding
    /// technically catches these earlier, but any manual construction of a SystemId
    /// (test fixtures, migration scripts) that happens to hand a blank string must not
    /// blow up the self-guard.
    /// </summary>
    [Test]
    public async Task SystemId_BlankValue_ReturnsFalse()
    {
        var candidate = new SystemId(string.Empty);

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsFalse()
            .Because("Blank candidates cannot represent any user — the primitive must return false rather than throw so the guard is safe on defensive inputs.");
    }

    // ---------------- UsernameOrSystemId overload ----------------

    /// <summary>
    /// The Send fast-path: a client sending their own scoped id as the route segment must
    /// self-reject without a repository hop. Preserves the pre-Round-2 fast-path semantics
    /// while replacing the fragile raw string compare.
    /// </summary>
    [Test]
    public async Task UsernameOrSystemId_ScopedSelf_IsSelf()
    {
        var candidate = new UsernameOrSystemId("nam:abcdefg");

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsTrue()
            .Because("A scoped self-id sent as the route segment is the trivial fast-path the Send controller must catch; behaviour byte-compatible with the pre-Round-2 byte compare.");
    }

    /// <summary>
    /// The pre-Round-2 miss on the Send path: a client sending their own raw id. Slice-7
    /// documented this as intentional (downstream handler catches it via the resolved-id
    /// self-check), but a controller-level catch produces the crisper cannot_send_self
    /// error without a repository hop, and is a safe strict-improvement.
    /// </summary>
    [Test]
    public async Task UsernameOrSystemId_RawSelf_IsSelf()
    {
        var candidate = new UsernameOrSystemId("abcdefg");

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsTrue()
            .Because("A bare raw-id route parses as LookupKind.Id and must delegate to the SystemId primitive's raw-id branch; this is a strict improvement over the pre-Round-2 fall-through-to-domain-handler behaviour.");
    }

    /// <summary>
    /// A username shape (<c>username:alice</c>) must NEVER falsely self-reject — the
    /// controller has no way to know whether "alice" resolves to this principal without
    /// hitting the registry. Delegating to the downstream resolver keeps the self-check
    /// correct for the case where the principal's own username is sent as the route.
    /// </summary>
    [Test]
    public async Task UsernameOrSystemId_UsernameShape_IsNotSelf()
    {
        var candidate = new UsernameOrSystemId("username:alice");

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsFalse()
            .Because("The controller cannot decide 'is alice me?' without a registry lookup; the fast-path must return false and let the downstream resolved-id self-check take over.");
    }

    /// <summary>
    /// Discord shapes have the same "requires registry lookup" property as usernames.
    /// </summary>
    [Test]
    public async Task UsernameOrSystemId_DiscordShape_IsNotSelf()
    {
        var candidate = new UsernameOrSystemId("discord:1234567");

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsFalse()
            .Because("Discord snowflake shapes require a registry lookup to resolve to a system id; the fast-path returns false.");
    }

    /// <summary>
    /// An unknown non-region prefix (<c>"xxx:abcdefg"</c>) is neither a system-id shape
    /// nor a discriminator-prefixed handle. LookupHandle.TryParse returns false for these,
    /// and the overload falls back to a byte-level compare that catches only the case
    /// where the client happened to send this principal's exact scoped composite. Pinned
    /// so a future LookupHandle behaviour change doesn't silently promote arbitrary
    /// prefixes into a coerce-then-self spelling.
    /// </summary>
    [Test]
    public async Task UsernameOrSystemId_UnknownPrefix_IsNotSelf()
    {
        var candidate = new UsernameOrSystemId("xxx:abcdefg");

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsFalse()
            .Because("Unknown non-region prefixes are opaque to the controller fast-path; the downstream resolver rejects them separately as friend_request:no_user.");
    }

    /// <summary>
    /// Blank input returns false rather than throwing. Route binding catches empty
    /// segments earlier, but the overload must be safe on manually-constructed inputs
    /// too — never let a defensive-input path throw.
    /// </summary>
    [Test]
    public async Task UsernameOrSystemId_BlankValue_ReturnsFalse()
    {
        var candidate = new UsernameOrSystemId(string.Empty);

        await Assert.That(Principal.RepresentsSameUserAs(candidate)).IsFalse()
            .Because("Blank candidates cannot represent any user — the primitive must return false rather than throw.");
    }
}
