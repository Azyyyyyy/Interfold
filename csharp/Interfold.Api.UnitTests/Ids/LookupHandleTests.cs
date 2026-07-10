using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.Ids;

/// <summary>
/// Pins the routing table <see cref="LookupHandle.TryParse"/> exposes to its two
/// consumer sites (<c>ScyllaUserRegistryRegionContext.LookupAsync</c> and the
/// friendship <c>ResolveUserId</c> paths). The unit is pure C# — no host, no IO —
/// so this is the fast tier. The IO-bound consequences of these routings are pinned
/// by <c>ResolveUserIdDispatchTests</c> (in-process fakes) and the integration
/// <c>SendFriendRequestPrefixTests</c> (real backends).
/// </summary>
public sealed class LookupHandleTests
{
    // ---------------- Region prefixes (all seven canonical tags) ------------

    [Test]
    [Arguments("nam:abcdefg")]
    [Arguments("eur:abcdefg")]
    [Arguments("sam:abcdefg")]
    [Arguments("sas:abcdefg")]
    [Arguments("eas:abcdefg")]
    [Arguments("ocn:abcdefg")]
    [Arguments("gdpr:abcdefg")]
    public async Task TryParse_RegionPrefix_KindRegion_RawIdStripsPrefix(string input)
    {
        var parsed = LookupHandle.TryParse(input, out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(handle.Kind).IsEqualTo(LookupKind.Region)
                .Because("Region-tagged handles must dispatch through the Region branch so the caller queries user_registry.user_id with the bare id.");
            await Assert.That(handle.RawId).IsEqualTo("abcdefg")
                .Because("RawId exposes the after-colon id — the region prefix is metadata, not part of the lookup value.");
            await Assert.That(handle.OriginalValue).IsEqualTo(input)
                .Because("OriginalValue is retained verbatim for logging / audit — the strip only affects RawId.");
        }
    }

    // ---------------- Discriminator prefixes --------------------------------

    [Test]
    public async Task TryParse_UsernamePrefix_KindUsername()
    {
        var parsed = LookupHandle.TryParse("username:alice", out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(handle.Kind).IsEqualTo(LookupKind.Username);
            await Assert.That(handle.RawId).IsEqualTo("alice");
        }
    }

    [Test]
    public async Task TryParse_DiscordPrefix_KindDiscord()
    {
        var parsed = LookupHandle.TryParse("discord:1234567890", out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(handle.Kind).IsEqualTo(LookupKind.Discord);
            await Assert.That(handle.RawId).IsEqualTo("1234567890");
        }
    }

    [Test]
    public async Task TryParse_IdPrefix_KindId_StripsPrefix()
    {
        var parsed = LookupHandle.TryParse("id:abcdefg", out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(handle.Kind).IsEqualTo(LookupKind.Id)
                .Because("Explicit id: prefix is the strict assertion that the value is a system id — same registry column as bare-id but caller made the intent explicit.");
            await Assert.That(handle.RawId).IsEqualTo("abcdefg");
        }
    }

    // ---------------- Bare id (no colon) ------------------------------------

    [Test]
    public async Task TryParse_BareId_KindId_RawIdIsWholeInput()
    {
        var parsed = LookupHandle.TryParse("abcdefg", out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(handle.Kind).IsEqualTo(LookupKind.Id);
            await Assert.That(handle.RawId).IsEqualTo("abcdefg")
                .Because("A bare handle has no prefix to strip — RawId is the whole input.");
        }
    }

    // ---------------- Case-insensitivity on discriminator prefixes ----------

    [Test]
    [Arguments("USERNAME:alice", LookupKind.Username)]
    [Arguments("Discord:1234", LookupKind.Discord)]
    [Arguments("ID:abcdefg", LookupKind.Id)]
    public async Task TryParse_DiscriminatorPrefix_CaseInsensitive(string input, LookupKind expected)
    {
        var parsed = LookupHandle.TryParse(input, out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue()
                .Because("Discriminator prefixes are normalised case-insensitively — a client that shouts USERNAME must dispatch identically to the lowercase form.");
            await Assert.That(handle.Kind).IsEqualTo(expected);
        }
    }

    // ---------------- Strict rejection --------------------------------------

    [Test]
    [Arguments((string?)null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task TryParse_NullOrBlank_ReturnsFalse(string? input)
    {
        var parsed = LookupHandle.TryParse(input, out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsFalse();
            await Assert.That(handle).IsEqualTo(default(LookupHandle));
        }
    }

    [Test]
    [Arguments(":abcdefg")]       // empty prefix
    [Arguments("nam:")]           // bare region prefix, no raw
    [Arguments("username:")]      // bare discriminator prefix, no raw
    [Arguments("discord:")]       // bare discriminator prefix, no raw
    [Arguments("id:")]            // bare discriminator prefix, no raw
    public async Task TryParse_BarePrefixOrEmptyHalf_ReturnsFalse(string input)
    {
        var parsed = LookupHandle.TryParse(input, out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsFalse()
                .Because($"'{input}' has one half empty — never dispatch a partially-formed handle to a registry column.");
            await Assert.That(handle).IsEqualTo(default(LookupHandle));
        }
    }

    [Test]
    [Arguments("xxx:abcdefg")]
    [Arguments("phone:0123456789")]
    [Arguments("bogus:whatever")]
    public async Task TryParse_UnknownPrefix_ReturnsFalse(string input)
    {
        var parsed = LookupHandle.TryParse(input, out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsFalse()
                .Because($"'{input}' has an unknown non-region prefix — callers must fall back to the opaque-bare-id path with the WHOLE input rather than silently stripping an unknown prefix.");
            await Assert.That(handle).IsEqualTo(default(LookupHandle));
        }
    }

    [Test]
    public async Task TryParse_MultipleColons_TreatsFirstColonAsPrefixSeparator()
    {
        // "username:foo:bar" → prefix "username", rawId "foo:bar". The rawId can itself
        // contain colons (e.g. a URI-like handle). The parser only ever splits on the
        // first colon; anything after belongs to RawId.
        var parsed = LookupHandle.TryParse("username:foo:bar", out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(handle.Kind).IsEqualTo(LookupKind.Username);
            await Assert.That(handle.RawId).IsEqualTo("foo:bar")
                .Because("The parser splits on the first colon only so a rawId can carry embedded colons (URI-like handles).");
        }
    }
}
