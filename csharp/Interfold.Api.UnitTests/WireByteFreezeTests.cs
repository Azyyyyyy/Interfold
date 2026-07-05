using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interfold.Api.Socket;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Events;
using Interfold.Contracts.Ids;
using Interfold.Domain;
using Interfold.Infrastructure;
using Interfold.Infrastructure.Coordination;

namespace Interfold.Api.UnitTests;

/// <summary>
/// Slice 4 golden-byte guardrail. Each fact below pins one wire boundary that a stray
/// <see cref="ScopedSystemId"/>.<c>RawId</c> swap (or any regression that de-scopes an id
/// before it hits persistence, a JWT, an event bus filter, or a socket topic) would
/// silently corrupt. If any of these fail, do not "adjust the expected value" — treat it
/// as a real wire-format break and audit the diff for the offending site.
/// </summary>
public sealed class WireByteFreezeTests
{
    // Slice 4's canonical shape: "nam:abcdefg" is the byte-form every wire boundary emits
    // for a NAM-region principal. Any test in this file that constructs an id uses this
    // as the reference so a search for the literal points every reviewer at the same
    // spot.
    private const string CanonicalScoped = "nam:abcdefg";
    private const string CanonicalRaw = "abcdefg";

    // ---------------- 1. EncryptionKey.DeriveKey — golden path ---------------------

    /// <summary>
    /// DeriveKey is a KDF: same inputs must produce the same output, and any accidental
    /// change to the wire form of <c>systemId</c> (e.g. handing the raw id to the KDF
    /// instead of the scoped one) would orphan every existing recovery code. This test
    /// pins:
    ///   * determinism across two calls with the same inputs, and
    ///   * scoped-vs-raw sensitivity — dropping the region prefix changes the derived key.
    /// The exact bytes are re-derived each run rather than hard-coded so a Konscious
    /// upstream tweak can't lock us into a stale expectation; the scoped-vs-raw
    /// inequality below is the wire-form freeze.
    /// </summary>
    [Test]
    public async Task DeriveKey_IsDeterministic_AndScopeSensitive()
    {
        const string pepper = "test-pepper";
        const string recoveryCode = "test-code";
        var salt = Convert.ToBase64String(Encoding.UTF8.GetBytes("known-salt-16b!!"));

        var first = EncryptionKey.DeriveKey(pepper, CanonicalScoped, recoveryCode, salt);
        var second = EncryptionKey.DeriveKey(pepper, CanonicalScoped, recoveryCode, salt);
        var rawOnly = EncryptionKey.DeriveKey(pepper, CanonicalRaw, recoveryCode, salt);

        await Assert.That(first).IsEqualTo(second)
            .Because("DeriveKey must be deterministic — a diff between two calls with identical inputs means Argon2 params or salt handling drifted.");
        await Assert.That(first).IsNotEqualTo(rawOnly)
            .Because("Dropping the region prefix from the systemId argument must change the derived key — otherwise a caller could silently swap scoped for raw and orphan the recovery flow.");
        await Assert.That(Convert.FromBase64String(first).Length).IsEqualTo(32)
            .Because("Argon2id hash_len is pinned at 32 in EncryptionKey.DeriveKey.");
    }

    // ---------------- 2. AuthHelper.CreateToken — JWT sub emission -----------------

    /// <summary>
    /// The JWT <c>sub</c> claim is the one place a scoped-id string crosses the trust
    /// boundary out of the process; <c>InterfoldPrincipalMiddleware</c> requires it to
    /// arrive back as a scoped composite on the inbound side. This test pins that
    /// <c>AuthHelper.CreateToken</c> emits the composite verbatim when handed a
    /// <see cref="SystemId"/> whose <c>Value</c> is the scoped form.
    /// </summary>
    [Test]
    public async Task CreateToken_EmitsScopedSubClaim()
    {
        var (privatePem, _) = GenerateEs256Pem();
        var authConfig = new AuthenticationConfiguration
        {
            JwtAuthority = "https://test.interfold.local",
            JwtEs256PrivateKeyPem = privatePem,
        };
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var expiresAt = now.AddDays(1);
        var jti = new Jti("golden-jti");
        var scoped = ScopedSystemId.Compose(ScyllaKeyspace.Nam, CanonicalRaw);

        var token = AuthHelper.CreateToken(authConfig, expiresAt, now, jti, scoped.AsSystemId());

        var payload = DecodeJwtPayload(token);
        await Assert.That(payload.GetProperty("sub").GetString()).IsEqualTo(CanonicalScoped)
            .Because("The JWT sub claim is a wire boundary; the middleware ParseScoped requires the region prefix and would 401 an unscoped emission.");
    }

    // ---------------- 3. SystemTopic.ToWireString — Phoenix topic ------------------

    /// <summary>
    /// Phoenix topics are matched as opaque strings on the socket. If ToWireString drops
    /// the region prefix (e.g. because someone swapped <c>Value</c> for <c>RawId</c>), the
    /// subscriber joined on the scoped topic never matches the publisher's payload and
    /// every downstream projection event goes silently unread.
    /// </summary>
    [Test]
    public async Task SystemTopic_ToWireString_EmitsScopedComposite()
    {
        var scoped = ScopedSystemId.Compose(ScyllaKeyspace.Nam, CanonicalRaw);
        var topic = new SystemTopic(scoped.AsSystemId());

        await Assert.That(topic.ToWireString()).IsEqualTo($"system:{CanonicalScoped}")
            .Because("SystemTopic emits the scoped composite verbatim; any change to that prefix breaks Phoenix topic matching for existing sockets.");
    }

    // ---------------- 4. InProcessEventBus — idempotent routing --------------------

    /// <summary>
    /// The whole point of Slice 4 for the event bus is that a subscriber joined on the
    /// raw form still receives events published with the scoped form (and vice versa).
    /// Before Slice 4 this was a silent-drop hazard: <c>SystemId != SystemId</c> compared
    /// raw-vs-scoped strings byte-for-byte and dropped every non-matching pair. Now the
    /// filter normalises through <c>SystemIdNormalization.StripRegionPrefix</c> so the
    /// scoped/raw diagonal always routes.
    /// </summary>
    [Test]
    public async Task InProcessEventBus_RoutesScopedAndRawInterchangeably()
    {
        using var bus = new InProcessEventBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Subscriber joined on the raw form — mirrors the WebSocket topic Id which is
        // captured from the topic string ("system:sys-xxx"), i.e. without the region
        // prefix.
        var subscriber = bus.SubscribeAsync<AlterCreatedEvent>(
            targetSystemId: new SystemId(CanonicalRaw),
            ct: cts.Token);

        var enumerator = subscriber.GetAsyncEnumerator(cts.Token);

        // Publisher emits with the scoped composite — mirrors every post-Slice-4
        // publisher that composes off command.PrincipalId.Region + raw id.
        var scopedTarget = ScopedSystemId.Compose(ScyllaKeyspace.Nam, CanonicalRaw);
        await bus.PublishAsync(new AlterCreatedEvent(scopedTarget, new AlterId(42)), cts.Token);

        var moved = await enumerator.MoveNextAsync();
        await Assert.That(moved).IsTrue()
            .Because("The scoped publish must reach a subscriber joined on the raw id — otherwise the bus silently drops every event whose publisher and subscriber disagree on the prefix, which is the Slice 4 regression this filter guards against.");
        await Assert.That(enumerator.Current.TargetSystemId.Value).IsEqualTo(CanonicalScoped)
            .Because("The delivered event must carry the scoped composite verbatim — the filter is region-tolerant on match, not lossy on payload.");

        await enumerator.DisposeAsync();
    }

    // ---------------- 5. ScopedSystemId JSON — verbatim wire form ------------------

    /// <summary>
    /// The JSON converter must emit exactly <c>Value</c> (no object wrapper). Any change
    /// here re-serialises every scoped-id payload in the wire contract set (command
    /// envelopes, event payloads) and would break clients that don't decode the wrapped
    /// form. This is the byte-freeze on the JSON boundary.
    /// </summary>
    [Test]
    public async Task ScopedSystemId_JsonRoundTrip_IsByteExact()
    {
        var scoped = ScopedSystemId.Compose(ScyllaKeyspace.Nam, CanonicalRaw);

        var json = JsonSerializer.Serialize(scoped);
        var roundTripped = JsonSerializer.Deserialize<ScopedSystemId>(json);

        await Assert.That(json).IsEqualTo($"\"{CanonicalScoped}\"")
            .Because("The converter must emit Value verbatim so the wire bytes match the pre-Slice-4 SystemId serialisation.");
        await Assert.That(roundTripped.Value).IsEqualTo(CanonicalScoped)
            .Because("A round-trip must preserve Value exactly; a divergence here means the converter reserialised through RawId or an object shape.");
    }

    // ---------------- helpers ------------------------------------------------------

    private static (string PrivatePem, string PublicPem) GenerateEs256Pem()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportECPrivateKeyPem(), ecdsa.ExportSubjectPublicKeyInfoPem());
    }

    private static JsonElement DecodeJwtPayload(string token)
    {
        var parts = token.Split('.');
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        using var doc = JsonDocument.Parse(payloadJson);
        return doc.RootElement.Clone();
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
