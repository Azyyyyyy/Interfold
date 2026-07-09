using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Infrastructure.InMemory;

namespace Interfold.Api.UnitTests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="InMemoryRegionContext"/> — the deterministic region resolver
/// used by every InMemory repository to derive the per-system partition key.
///
/// <para>
/// Background: Slice 4 hardened the middleware/JWT layer so principal ids reach the
/// persistence adapters in the scoped <c>{region}:{rawId}</c> wire form, while route-bound
/// ids on public read endpoints stay in the raw form (a <c>[FromRoute] SystemId</c> binding
/// wraps the URL segment verbatim). The pre-fix resolver hashed whichever shape it was
/// handed, which produced a different region for the two shapes of the same principal —
/// row was written under region X's partition key and read from region Y's, surfacing as
/// <c>system_not_found</c> 404s in <c>PublicSystemsController</c> for every test that
/// seeds via the JWT-derived principal and reads through a raw URL segment (the entire
/// avatar / fronting / visibility / public-batch / field-security cluster).
/// </para>
///
/// <para>
/// This suite pins the region-strip-before-hash invariant so a future refactor that peels
/// the normalisation back breaks in the fast unit-test tier, not inside a full-suite
/// integration run where the 404 looks like a controller bug rather than a resolver one.
/// The equivalent invariant on the persistent backend already has coverage in
/// <c>RegionContextCachingTests.ResolveUserRegion_StripsLegacyPrefix_BeforeCacheLookup</c>.
/// </para>
/// </summary>
public sealed class InMemoryRegionContextTests
{
    // A representative raw system id — pinned as a constant so the tolerance-matrix tests
    // below all reason about the same principal and any per-process hash randomisation
    // (String.GetHashCode is randomised per AppDomain in modern .NET) affects them
    // uniformly rather than differently.
    private const string RawId = "sys-region-context-test";

    // ------------------------------------------------------------------------------------
    // The load-bearing tolerance: scoped vs raw shapes of the same principal must resolve
    // to the same region. This is the invariant the whole 12-test integration cluster
    // depended on and that the pre-fix resolver violated.
    // ------------------------------------------------------------------------------------

    [Test]
    public async Task ScopedAndRawFormsOfSamePrincipal_ResolveToSameRegion()
    {
        var ctx = new InMemoryRegionContext();

        var scoped = ctx.ResolveUserRegion($"nam:{RawId}");
        var raw = ctx.ResolveUserRegion(RawId);

        await Assert.That(scoped).IsEqualTo(raw)
            .Because("A scoped nam:{rawId} JWT-derived principal and a raw {rawId} route-bound principal must land in the same region — otherwise InMemoryStorageKeys.ForSystem yields two different partition keys for the same user and public reads 404 with system_not_found (the entire post-Slice-4 avatar/fronting/visibility cluster failure).");
    }

    [Test]
    public async Task CrossRegionSameRawId_ResolveToSameRegion()
    {
        var ctx = new InMemoryRegionContext();

        // Two scoped forms of the same underlying id — different region prefix, same raw
        // suffix. StripRegionPrefix removes both prefixes symmetrically, so the hash sees
        // the same string in both cases. This pins that the resolver does NOT gate on the
        // region prefix (it's purely a canonicalisation of the id, not a region filter).
        var namScoped = ctx.ResolveUserRegion($"nam:{RawId}");
        var eurScoped = ctx.ResolveUserRegion($"eur:{RawId}");

        await Assert.That(namScoped).IsEqualTo(eurScoped)
            .Because("The region prefix is stripped, not gated on — a caller that happens to hold the id under a different region wire tag (rare but possible across the seven canonical regions) must not partition into a different physical bucket.");
    }

    // Note: single-vs-recursive strip semantics live in ScopedSystemId.StripLeadingRegionPrefix
    // and are covered by that type's own unit tests. Adding a "double-scoped" pin here would
    // duplicate that coverage and — because we can't observe the intermediate strip residue
    // from the outside without a pinned hash — could only assert probabilistic properties
    // over the 7-region distribution. Leaving it out keeps this file focused on the
    // scoped-vs-raw tolerance that the InMemory persistence adapters actually depend on.

    // ------------------------------------------------------------------------------------
    // Still actually discriminating: the normalisation must not accidentally collapse
    // every id into a single region. Uses a large sample so per-process hash randomisation
    // can't produce a run where every candidate coincidentally lands in one region (with
    // seven regions and 50 samples, all-same probability is 7 * (1/7)^50 ≈ 4e-42).
    // ------------------------------------------------------------------------------------

    [Test]
    public async Task DifferentPrincipals_ResolveAcrossMultipleRegions()
    {
        var ctx = new InMemoryRegionContext();

        var observed = new HashSet<ScyllaKeyspace>();
        for (var i = 0; i < 50; i++)
        {
            observed.Add(ctx.ResolveUserRegion($"sys-discriminator-{i:D4}"));
        }

        await Assert.That(observed.Count).IsGreaterThan(1)
            .Because("The resolver must still discriminate between principals — if it degenerates to a single region for every id (e.g. by hashing a constant, or by falling through to CurrentRegion for non-empty inputs), the InMemory backend loses its multi-region routing and every test sharing a WebApplicationFactory sees a single-partition collision surface.");
    }

    // ------------------------------------------------------------------------------------
    // Preserve the existing early-return branch for blank inputs. The pre-fix code
    // short-circuited to CurrentRegion for null / empty / whitespace, and downstream
    // callers (particularly Scylla's ScyllaKeyspaceResolver.ResolveRegionalKeyspace) rely
    // on that default rather than throwing.
    // ------------------------------------------------------------------------------------

    [Test]
    public async Task NullInput_FallsBackToCurrentRegion()
    {
        var ctx = new InMemoryRegionContext(currentRegion: ScyllaKeyspace.Eur);

        var region = ctx.ResolveUserRegion(systemId: (string)null!);

        await Assert.That(region).IsEqualTo(ScyllaKeyspace.Eur)
            .Because("Null id must fall through to the constructor-supplied CurrentRegion — the ScyllaKeyspaceResolver default path depends on this and would throw otherwise.");
    }

    [Test]
    public async Task EmptyInput_FallsBackToCurrentRegion()
    {
        var ctx = new InMemoryRegionContext(currentRegion: ScyllaKeyspace.Sam);

        var region = ctx.ResolveUserRegion(string.Empty);

        await Assert.That(region).IsEqualTo(ScyllaKeyspace.Sam)
            .Because("Empty id is the second IsNullOrWhiteSpace shape — pin all three (null, empty, whitespace) so a refactor to a single guard clause can't silently drop one branch.");
    }

    [Test]
    public async Task WhitespaceInput_FallsBackToCurrentRegion()
    {
        var ctx = new InMemoryRegionContext(currentRegion: ScyllaKeyspace.Sas);

        var region = ctx.ResolveUserRegion("   ");

        await Assert.That(region).IsEqualTo(ScyllaKeyspace.Sas)
            .Because("Whitespace-only id completes the IsNullOrWhiteSpace matrix.");
    }

    // ------------------------------------------------------------------------------------
    // The two overloads (string and SystemId) share the same implementation via delegation
    // (SystemId overload calls the string one with .Value). Pin that delegation so a future
    // "specialise the typed path" refactor keeps both surfaces in lockstep — otherwise a
    // caller holding a SystemId and a caller holding its .Value could diverge.
    // ------------------------------------------------------------------------------------

    [Test]
    public async Task TypedOverload_AgreesWithStringOverload()
    {
        var ctx = new InMemoryRegionContext();
        var typed = new SystemId($"nam:{RawId}");

        var fromTyped = ctx.ResolveUserRegion(typed);
        var fromString = ctx.ResolveUserRegion(typed.Value);

        await Assert.That(fromTyped).IsEqualTo(fromString)
            .Because("The SystemId overload delegates to the string one — parity must hold so a caller that promotes a raw string to a typed SystemId at some ingress boundary doesn't accidentally hop to a different partition.");
    }
}
