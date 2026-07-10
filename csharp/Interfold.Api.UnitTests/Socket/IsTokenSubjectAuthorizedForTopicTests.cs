using Interfold.Api.Socket;
using Interfold.Contracts.Ids;

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
/// Step 5 of the strong-typing rescan tightened the helper's contract to
/// <see cref="ScopedSystemId"/> on the sub side and <see cref="SystemId"/> on the topic
/// side. The historical "raw sub × raw topic" tolerance the pre-Step-5 helper encoded is
/// now a rejection at <see cref="ScopedSystemId.TryParseScoped"/> (the caller in
/// <c>IsSocketJoinTokenAuthorizedAsync</c> returns <c>InvalidSocketTokenSubject</c> and
/// never reaches this helper); the rejection-matrix coverage moved upstream to
/// <c>ScopedSystemIdTests.TryParseScoped_InvalidInputs_ReturnsFalse</c>, whose
/// <c>[Arguments]</c> table pins every rejection shape the middleware also rejects
/// (null / blank / bare id / empty region / bare prefix / unknown region tag / non-region
/// discriminator prefix). That means the six "raw sub" or "malformed sub" cases the pre-
/// Step-5 suite carried are now type-unreachable and have been deleted — a raw-string sub
/// simply can't be constructed as a call argument any more.
/// </para>
///
/// <para>
/// What remains here is the actual comparison contract: scoped-sub vs raw/scoped topic
/// tolerance (topics still arrive in either shape from clients), cross-region tolerance,
/// same-shape identity rejection, and the two null guards. The suite pins the tolerance
/// matrix so a future edit that drops the normalisation (regressing to raw string
/// equality) breaks in the fast unit-test tier before it can hide again inside a 30-
/// second WebSocket timeout.
/// </para>
/// </summary>
public sealed class IsTokenSubjectAuthorizedForTopicTests
{
    // A representative raw system id — mirrors what UniqueId("sys-...") produces at the
    // integration-test layer, but pinned here so a rename of the test generator can't
    // silently change what shape this suite is exercising.
    private const string RawId = "sys-abcdef0123456789";

    // ------------------------------------------------------------------------------------
    // The tolerance matrix: scoped sub × raw/scoped topic. The pre-Step-5 raw-sub cells
    // are gone (type-unreachable via ScopedSystemId?), so the 4-cell matrix collapses to
    // these 2 rows.
    // ------------------------------------------------------------------------------------

    [Test]
    public async Task ScopedSub_RawTopic_SameRawId_IsAuthorized()
    {
        var scopedSub = ScopedSystemId.ParseScoped($"nam:{RawId}");
        var topic = new SystemId(RawId);

        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(scopedSub, topic);

        await Assert.That(authorized).IsTrue()
            .Because("Post-Slice-4 JWTs carry a scoped nam:sys-... sub while the socket topic stays raw — this is the exact shape that used to 401 the loopback endpoint proxy path and that Slice-4 fixed by strip-tolerant equality.");
    }

    [Test]
    public async Task ScopedSub_ScopedTopic_SameRawId_IsAuthorized()
    {
        var scopedSub = ScopedSystemId.ParseScoped($"nam:{RawId}");
        var topic = new SystemId($"nam:{RawId}");

        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(scopedSub, topic);

        await Assert.That(authorized).IsTrue()
            .Because("Scoped-on-both-sides is the wire shape once every client sends fully-qualified topics — the topic-side StripRegionPrefix collapses onto RawId and equality holds.");
    }

    // ------------------------------------------------------------------------------------
    // Cross-region: the topic-side strip peels ANY known region prefix, so the same raw id
    // under different regions still authorises. This matches SystemTopic.IdMatches and
    // InProcessEventBus.PublishAsync — no region gate lives here.
    // ------------------------------------------------------------------------------------

    [Test]
    public async Task ScopedSub_DifferentRegionOnTopic_SameRawId_IsAuthorized()
    {
        var scopedSub = ScopedSystemId.ParseScoped($"nam:{RawId}");
        var topic = new SystemId($"eur:{RawId}");

        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(scopedSub, topic);

        await Assert.That(authorized).IsTrue()
            .Because("Region is stripped from the topic side (sub side already carries an authoritative RawId). Enforcing region equality is not this helper's job — that's what the region-context lookup / persistence layer decides.");
    }

    // ------------------------------------------------------------------------------------
    // The negative cases: different raw ids must fail, regardless of the topic's prefix
    // shape. These pin the "still actually checking identity" contract so the tolerance
    // can't decay into "always true".
    // ------------------------------------------------------------------------------------

    [Test]
    public async Task ScopedSub_RawTopic_DifferentRawIds_IsRejected()
    {
        var scopedSub = ScopedSystemId.ParseScoped($"nam:{RawId}");
        var topic = new SystemId("sys-someone-else");

        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(scopedSub, topic);

        await Assert.That(authorized).IsFalse()
            .Because("Sub's RawId and topic's stripped RawId must actually match — the strip on either side is a canonicalisation, not a wildcard.");
    }

    [Test]
    public async Task ScopedSub_ScopedTopic_DifferentRawIds_IsRejected()
    {
        var scopedSub = ScopedSystemId.ParseScoped($"nam:{RawId}");
        var topic = new SystemId("nam:sys-someone-else");

        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(scopedSub, topic);

        await Assert.That(authorized).IsFalse()
            .Because("Same region prefix on the topic side must not mask a raw-id mismatch.");
    }

    // ------------------------------------------------------------------------------------
    // Defence-in-depth: null inputs on either side return false rather than throwing. The
    // upstream caller in IsSocketJoinTokenAuthorizedAsync already returns
    // InvalidSocketTokenSubject on a null-scoped-sub (TryParseScoped failed) and never
    // enters this helper with a null topic (SystemTopic.TryParse-fail branch passes null
    // and the caller returns UnauthorizedTopic without invoking us). But keeping the
    // guards means unit tests / any future non-socket caller doesn't need to hand-craft a
    // "non-null" precondition.
    //
    // Note: the pre-Step-5 sub-side null/empty/whitespace/unknown-prefix rejection tests
    // are gone — those inputs can't be constructed as a ScopedSystemId in the first place.
    // See ScopedSystemIdTests.TryParseScoped_InvalidInputs_ReturnsFalse for the moved-
    // upstream rejection matrix.
    // ------------------------------------------------------------------------------------

    [Test]
    public async Task NullScopedSub_IsRejected()
    {
        var topic = new SystemId(RawId);

        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(tokenSubject: null, requestedSystemId: topic);

        await Assert.That(authorized).IsFalse()
            .Because("Null scoped-sub can never authorise — the upstream gate treats a failed TryParseScoped as InvalidSocketTokenSubject and returns before we're called, and the defence-in-depth null guard here is what backs that upstream contract for any future caller.");
    }

    [Test]
    public async Task NullRequestedSystemId_IsRejected()
    {
        var scopedSub = ScopedSystemId.ParseScoped($"nam:{RawId}");

        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(tokenSubject: scopedSub, requestedSystemId: null);

        await Assert.That(authorized).IsFalse()
            .Because("A null requested id (non-system topic, or SystemTopic.TryParse returning false) must not authorise — mirror the sub-side null guard.");
    }
}
