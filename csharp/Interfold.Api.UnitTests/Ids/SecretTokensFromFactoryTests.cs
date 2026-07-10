using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.Ids;

/// <summary>
/// Pins the <c>Jti.From(string?)</c> and <c>LinkToken.From(string?)</c> factories added in
/// Step 7 of the strong-typing rescan.
///
/// <para>
/// Pre-Step-7, the two callers (<c>AuthController.RevokeToken</c> for Jti,
/// <c>AuthLinkController.Callback</c> for LinkToken) shared the exact same anti-pattern:
/// pull a possibly-null raw string out of a JWT claim or query/cookie fallback, null-check
/// on the bare string local, then <c>new Jti(...)</c> / <c>new LinkToken(...)</c> on the
/// very next line at the call site that needed the wrapper all along. That two-line
/// window kept the raw credential alive as an interpolable <c>string</c> across the guard
/// / error-return / wrap boundary — any incidental structured-log message or exception
/// wrapper containing the local would leak the token verbatim, entirely defeating the
/// point of the redacting wrapper.
/// </para>
///
/// <para>
/// The <c>From</c> factory closes that window by folding the "null-or-blank → null,
/// otherwise wrap" step into a single total function. The caller's null check then runs
/// on the typed nullable (<c>Jti?</c> / <c>LinkToken?</c>) and the raw string exists
/// only inside the factory's argument-evaluation frame.
/// </para>
///
/// <para>
/// This suite is deliberately small and targeted: the four-input matrix
/// (null / empty / whitespace-only / valid) for each of the two factories, exactly
/// mirroring what the two Step-7 call sites can throw at them in production. Broader
/// wrapper-shape tests (JSON round-trip, redaction behaviour) belong with the wrapper
/// itself; this file only pins the invariants the Step-7 rewrite depends on.
/// </para>
/// </summary>
public sealed class SecretTokensFromFactoryTests
{
    [Test]
    [Arguments((string?)null)]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("\t")]
    [Arguments("   ")]
    public async Task JtiFrom_NullOrBlankInput_ReturnsNull(string? input)
    {
        // AuthController.RevokeToken calls Jti.From(User.FindFirst(JwtClaimNames.Jti)?.Value).
        // FindFirst returns null when the claim is absent; .Value is null when the claim
        // exists but carries no value; a whitespace-only claim is a spec violation that the
        // guard also rejects. All three collapse to the same "missing JTI" bad-request path,
        // so From must map all three to null. If a future edit accepts a blank string here,
        // RevokeTokenAsync would proceed to persist a whitespace-keyed revocation row.
        var result = Jti.From(input);

        await Assert.That(result).IsNull()
            .Because($"'{input ?? "<null>"}' must not produce a wrapped Jti — the AuthController null-check depends on From returning null for missing / blank JWT claims so the BadRequest 'Token is missing JTI claim' path runs.");
    }

    [Test]
    public async Task JtiFrom_ValidInput_ReturnsWrappedValue()
    {
        // Positive path: a real JTI (typically a Guid or opaque token) must round-trip
        // through From → .Value with the string preserved bit-for-bit. Any transformation
        // here (trim, normalize case, base64-decode) would corrupt the revocation lookup
        // key and silently break logout invalidation.
        const string valid = "1a2b3c4d-5e6f-7890-abcd-ef0123456789";

        var result = Jti.From(valid);

        using (Assert.Multiple())
        {
            await Assert.That(result).IsNotNull()
                .Because("A non-blank JTI is a valid input and must wrap.");
            await Assert.That(result!.Value.Value).IsEqualTo(valid)
                .Because("From must not transform the input — the JTI is used verbatim as the revocation store key and any normalization would silently break logout invalidation across sessions.");
        }
    }

    [Test]
    [Arguments((string?)null)]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("\t")]
    [Arguments("   ")]
    public async Task LinkTokenFrom_NullOrBlankInput_ReturnsNull(string? input)
    {
        // AuthLinkController.Callback calls LinkToken.From(await GetValueAsync(...) ?? cookie).
        // Both the query-string extraction and the cookie fallback can return null (missing)
        // or empty (present-but-empty). A whitespace-only value is a copy-paste-mangled URL
        // or a hostile client. All three collapse to the same 403 "invalid or expired" path,
        // so From must reject all three symmetrically. If From accepted blank, the resolver
        // would issue a Scylla lookup with a whitespace key and (with an unlucky test seed)
        // could match a corrupted row.
        var result = LinkToken.From(input);

        await Assert.That(result).IsNull()
            .Because($"'{input ?? "<null>"}' must not produce a wrapped LinkToken — the AuthLinkController null-check depends on From returning null for the missing-query-AND-missing-cookie case so the 403 'invalid or expired' path runs instead of a phantom lookup.");
    }

    [Test]
    public async Task LinkTokenFrom_ValidInput_ReturnsWrappedValue()
    {
        // Positive path: a real link token (Guid-shaped, minted by GET /settings/link_token)
        // must round-trip through From → .Value verbatim so the Scylla / InMemory account
        // repository's ResolveSystemIdByLinkTokenAsync uses the same key that was written
        // by the mint endpoint. Any From-side transformation would break the whole one-time-
        // link handshake with the exact "invalid or expired" symptom Step 7 tries to prevent
        // in the missing-input case.
        const string valid = "97e6d3ab-cbfe-4a30-8f7e-6b2c8a3d9e1f";

        var result = LinkToken.From(valid);

        using (Assert.Multiple())
        {
            await Assert.That(result).IsNotNull()
                .Because("A non-blank link token is a valid input and must wrap.");
            await Assert.That(result!.Value.Value).IsEqualTo(valid)
                .Because("From must not transform the input — the link token is used verbatim as the Scylla lookup key and any normalization would silently break the one-time-link handshake.");
        }
    }

    [Test]
    public async Task JtiFrom_WrappedValueRoundTripsThroughRedactedToString()
    {
        // Belt-and-braces on the safety story Step 7 is enforcing: after From returns a
        // wrapped Jti, an incidental $"{jti}" interpolation in a log statement between the
        // From call and the RevokeTokenAsync call must go through the redacting ToString().
        // If a future edit ever gives Jti a From that returns a struct with a plaintext-
        // exposing ToString override, this test fails and re-opens the leak window the
        // whole refactor was designed to close.
        const string valid = "eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCJ9.payload.signature";

        var result = Jti.From(valid);
        var interpolated = $"jti = {result?.ToString()}";

        using (Assert.Multiple())
        {
            await Assert.That(interpolated).DoesNotContain(valid)
                .Because("The wrapped Jti must redact under interpolation — the whole point of routing through the From factory is to ensure downstream string uses of the wrapped credential are safe by default.");
            await Assert.That(interpolated).StartsWith("jti = eyJh")
                .Because("Redaction is the first-4-chars + ellipsis form defined by SecretRedaction.Redact — pins the exact contract so a future 'safer' variant that returns '<redacted>' does not silently break log-parsing tools that grep for the prefix.");
        }
    }

    [Test]
    public async Task LinkTokenFrom_WrappedValueRoundTripsThroughRedactedToString()
    {
        // Symmetric belt-and-braces for LinkToken. The AuthLinkController Callback logs
        // several redirect / cookie state messages between the LinkToken.From call and the
        // ResolveSystemIdByLinkTokenAsync call; any of those can (now or in future) end up
        // interpolating the wrapped local. That must never leak the one-time link token
        // into a log line — the whole "wrap via From" idiom leans on this contract.
        const string valid = "0123456789abcdef0123456789abcdef";

        var result = LinkToken.From(valid);
        var interpolated = $"link_token = {result?.ToString()}";

        using (Assert.Multiple())
        {
            await Assert.That(interpolated).DoesNotContain(valid)
                .Because("The wrapped LinkToken must redact under interpolation — the AuthLinkController's cookie / redirect logs live in the same span as the From call and must not leak the token verbatim.");
            await Assert.That(interpolated).StartsWith("link_token = 0123")
                .Because("Pins the first-4-chars + ellipsis contract for LinkToken symmetrically with the Jti case.");
        }
    }
}
