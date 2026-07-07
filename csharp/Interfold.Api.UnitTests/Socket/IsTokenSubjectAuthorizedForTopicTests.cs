using Interfold.Api.Socket;

namespace Interfold.Api.UnitTests.Socket;

/// <summary>
/// Unit tests for <see cref="WebSocketHandler.IsTokenSubjectAuthorizedForTopic"/> — the
/// region-prefix-tolerant equality that gates a <c>phx_join</c> against the token's <c>sub</c>
/// claim.
///
/// <para>
/// Background: Slice 4 hardened <c>InterfoldPrincipalMiddleware</c> so every JWT that reaches
/// an Interfold controller must carry a scoped <c>{region}:{rawId}</c> sub. Socket topics on
/// the wire stay in raw <c>system:{rawId}</c> form. Before this helper existed, the join
/// gate did a strict ordinal comparison and would 401 a scoped-sub JWT joining a raw-topic
/// channel — even though the middleware + pump-side push routing (<c>SystemTopic.IdMatches</c>
/// and <c>InProcessEventBus.PublishAsync</c>'s filter) already tolerated the split. That
/// disagreement is the entire websocket-cluster failure surface: the loopback endpoint proxy
/// forwards the socket JWT to the inner controller, which 401s under strict comparison and
/// starves the pump of the events the test is waiting for.
/// </para>
///
/// <para>
/// This suite pins the tolerance matrix so a future edit that drops the normalisation
/// (regressing to raw string equality) breaks in the fast unit-test tier before it can hide
/// again inside a 30-second WebSocket timeout.
/// </para>
/// </summary>
public sealed class IsTokenSubjectAuthorizedForTopicTests
{
    // A representative raw system id — mirrors what UniqueId("sys-...") produces at the
    // integration-test layer, but pinned here so a rename of the test generator can't
    // silently change what shape this suite is exercising.
    private const string RawId = "sys-abcdef0123456789";

    // ------------------------------------------------------------------------------------
    // The tolerance matrix: raw/scoped sub × raw/scoped topic must all accept when the raw
    // ids match. These four cells are the whole point of the helper.
    // ------------------------------------------------------------------------------------

    [Test]
    public async Task RawSub_RawTopic_SameId_IsAuthorized()
    {
        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(RawId, RawId);
        await Assert.That(authorized).IsTrue()
            .Because("Raw sub against raw topic id must remain the baseline pass — this shape is what the join gate saw pre-Slice-4 and it must still work.");
    }

    [Test]
    public async Task ScopedSub_RawTopic_SameRawId_IsAuthorized()
    {
        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic($"nam:{RawId}", RawId);
        await Assert.That(authorized).IsTrue()
            .Because("Post-Slice-4 JWTs carry a scoped nam:sys-... sub while the socket topic stays raw — this is the exact shape that used to 401 the loopback endpoint proxy path.");
    }

    [Test]
    public async Task RawSub_ScopedTopic_SameRawId_IsAuthorized()
    {
        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(RawId, $"nam:{RawId}");
        await Assert.That(authorized).IsTrue()
            .Because("Mirror of the scoped-sub case — the gate must be symmetric so a legacy raw-sub client hitting a scoped topic (or a future scoped-topic client) doesn't 401.");
    }

    [Test]
    public async Task ScopedSub_ScopedTopic_SameRawId_IsAuthorized()
    {
        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic($"nam:{RawId}", $"nam:{RawId}");
        await Assert.That(authorized).IsTrue()
            .Because("Scoped-on-both-sides is the wire shape once every client is post-Slice-4 — must pass without special casing.");
    }

    // ------------------------------------------------------------------------------------
    // Cross-region: the helper strips ANY known region prefix on either side, so the same
    // raw id under different regions still authorises. This matches SystemTopic.IdMatches
    // and InProcessEventBus.PublishAsync — no region gate lives here.
    // ------------------------------------------------------------------------------------

    [Test]
    public async Task ScopedSub_DifferentRegionOnTopic_SameRawId_IsAuthorized()
    {
        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic($"nam:{RawId}", $"eur:{RawId}");
        await Assert.That(authorized).IsTrue()
            .Because("Region is stripped from both sides. Enforcing region equality is not this helper's job — that's what the region-context lookup / persistence layer decides.");
    }

    // ------------------------------------------------------------------------------------
    // The negative cases: different raw ids must fail, regardless of prefix combination.
    // These pin the "still actually checking identity" contract so the tolerance can't
    // decay into "always true".
    // ------------------------------------------------------------------------------------

    [Test]
    public async Task RawSub_RawTopic_DifferentIds_IsRejected()
    {
        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(RawId, "sys-someone-else");
        await Assert.That(authorized).IsFalse()
            .Because("Raw != raw with different ids must still fail — the tolerance is a region strip, not a wildcard.");
    }

    [Test]
    public async Task ScopedSub_RawTopic_DifferentRawIds_IsRejected()
    {
        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic($"nam:{RawId}", "sys-someone-else");
        await Assert.That(authorized).IsFalse()
            .Because("Stripping nam: from the sub must not collapse into equality with an unrelated raw topic id.");
    }

    [Test]
    public async Task ScopedSub_ScopedTopic_DifferentRawIds_IsRejected()
    {
        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic($"nam:{RawId}", "nam:sys-someone-else");
        await Assert.That(authorized).IsFalse()
            .Because("Same region prefix on both sides must not mask a raw-id mismatch.");
    }

    // ------------------------------------------------------------------------------------
    // StripRegionPrefix is conservative: only recognised region tags (nam/eur/sam/sas/eas/
    // ocn/gdpr) are stripped. An unrecognised prefix stays in place, which means a
    // "foo:bar" sub is compared verbatim to the topic. Pin this so an accidental widening
    // of the strip (e.g. "strip anything before the first colon") is caught here.
    // ------------------------------------------------------------------------------------

    [Test]
    public async Task UnknownRegionPrefix_OnSub_IsNotStripped_And_MismatchRejected()
    {
        // "foo" is not a recognised region tag, so the sub stays "foo:sys-..." verbatim.
        // The topic is the bare raw id — they don't match under ordinal comparison.
        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic($"foo:{RawId}", RawId);
        await Assert.That(authorized).IsFalse()
            .Because("An unknown prefix like 'foo:' must not be stripped — StripRegionPrefix only recognises the seven canonical region tags, and widening that would let arbitrary discriminator prefixes silently pass the gate.");
    }

    // ------------------------------------------------------------------------------------
    // Defence-in-depth: null / empty inputs return false rather than throwing. The
    // upstream caller in IsSocketJoinTokenAuthorizedAsync already checks for a missing sub
    // and returns InvalidSocketTokenSubject, but the helper being robust to null on its
    // own means unit tests that hit it in isolation don't need to hand-craft a "non-null"
    // guard on every call.
    // ------------------------------------------------------------------------------------

    [Test]
    public async Task NullTokenSubject_IsRejected()
    {
        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(tokenSubject: null, requestedSystemId: RawId);
        await Assert.That(authorized).IsFalse()
            .Because("Null sub can never authorise — the upstream gate treats this as InvalidSocketTokenSubject, and the helper must not accidentally normalise-away into equality with a whitespace requested id.");
    }

    [Test]
    public async Task EmptyTokenSubject_IsRejected()
    {
        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(tokenSubject: string.Empty, requestedSystemId: RawId);
        await Assert.That(authorized).IsFalse()
            .Because("Empty sub is semantically equivalent to null here — reject rather than fall through to a false positive after strip.");
    }

    [Test]
    public async Task WhitespaceTokenSubject_IsRejected()
    {
        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(tokenSubject: "   ", requestedSystemId: RawId);
        await Assert.That(authorized).IsFalse()
            .Because("Whitespace-only sub is the third IsNullOrWhiteSpace shape — pin all three so a refactor to a single guard clause can't drop one.");
    }

    [Test]
    public async Task NullRequestedSystemId_IsRejected()
    {
        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(tokenSubject: RawId, requestedSystemId: null);
        await Assert.That(authorized).IsFalse()
            .Because("A null requested id (non-system topic, or SystemTopic.TryParse returning false) must not authorise — mirror the sub-side guard.");
    }
}
