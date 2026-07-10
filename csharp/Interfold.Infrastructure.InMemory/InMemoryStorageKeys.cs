using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Infrastructure.InMemory;

/// <summary>
/// Shared key/normalization helpers for the InMemory repositories, replacing the
/// per-repository copies. The formats are frozen: normalization strips a legacy
/// <c>"region:"</c> prefix (mirroring <c>ScyllaKeyspaceResolver.NormalizeSystemId</c>) and
/// system partition keys are <c>"{region}:{systemId}"</c>.
/// </summary>
internal static class InMemoryStorageKeys
{
    /// <summary>
    /// Region-strip a <see cref="SystemId"/> and return a fresh <see cref="SystemId"/> so the
    /// typed key can be used directly as a dictionary key without unwrapping to
    /// <see cref="string"/>. Preferred entry point for the InMemory repositories that key
    /// by a normalized system id (friendship, encryption, notification, auth-revocation, ...).
    ///
    /// <para>
    /// Post-Round-5 dead-code sweep: two string-shaped siblings
    /// (<c>NormalizeSystemId(SystemId)</c> and <c>NormalizeSystemId(string)</c>) that
    /// predated Round 3's typed dictionary keys have been deleted — grep-verified zero
    /// callers across the solution. Any caller who genuinely needs the raw
    /// <see cref="string"/> can call <c>SystemIdNormalization.StripRegionPrefix</c>
    /// directly (which is the shim those overloads used to delegate to).
    /// </para>
    /// </summary>
    public static SystemId Normalize(SystemId systemId)
        => new(SystemIdNormalization.StripRegionPrefix(systemId.Value));

    /// <summary>
    /// The per-system dictionary partition key: <c>"{region}:{systemId}"</c>. Routed through
    /// <see cref="ScopedSystemId.Compose(ScyllaKeyspace, SystemId)"/> so the composition is
    /// idempotent — an incoming <see cref="SystemId"/> that already carries a region prefix
    /// no longer produces a double-prefixed key like <c>"nam:nam:abcdefg"</c>, which was the
    /// silent failure mode of the pre-Slice-4 hand-concatenation.
    ///
    /// <para>
    /// Round-3 Commit 1 (canvas #1): returns the typed <see cref="ScopedSystemId"/> directly
    /// so the seven InMemory repos can key their per-system dictionaries on the wrapper
    /// (which is a <c>readonly record struct</c> with ordinal equality on its underlying
    /// string — behaviourally identical to the pre-Round-3 raw-string key). Round-2 Commit
    /// 12 retyped the dictionary <b>values</b> to speak wrappers but left the keys as raw
    /// string because retyping this funnel was cross-cutting; this finishes that story.
    /// </para>
    /// </summary>
    public static ScopedSystemId ForSystem(IRegionContext regionContext, SystemId systemId)
    {
        var region = regionContext.ResolveUserRegion(systemId);
        return ScopedSystemId.Compose(region, systemId);
    }

    /// <summary>
    /// Resolve the friendship level between the <paramref name="systemId"/> owner and the
    /// (optional) <paramref name="viewerSystemId"/> viewer for the InMemory port. Returns
    /// <see langword="null"/> when there is no viewer (public read), returns
    /// <see cref="FriendshipLevel.TrustedFriend"/> when the viewer is the owner (self-read),
    /// and otherwise defers to the <paramref name="friendships"/> repository if one was
    /// wired for this InMemory instance. When <paramref name="friendships"/> is
    /// <see langword="null"/> (the AppHost bootstraps some InMemory instances with a
    /// null friendship repository), the method returns <see langword="null"/> — the
    /// caller-side "non-owner viewer without a friendship graph" branch that every InMemory
    /// alter / tag / fronting read model relies on.
    ///
    /// <para>
    /// Consolidated in the post-Round-4 sanity check from three byte-identical private
    /// helpers on <c>InMemoryAlterRepository</c>, <c>InMemoryTagRepository</c>, and
    /// <c>InMemoryFrontingRepository</c>. Round-4 finding #2 aligned the Tag and Fronting
    /// bodies with Alter's reference implementation — that alignment is what made this
    /// three-way collapse safe. Round-2 Commit 6 did the equivalent consolidation on the
    /// Scylla side by promoting <c>ResolveFriendshipLevelAsync</c> onto
    /// <c>ScyllaSharedQueries</c>; this method finishes that story on the InMemory port.
    /// </para>
    ///
    /// <para>
    /// The self-check normalises through <see cref="SystemIdNormalization.StripRegionPrefix"/>
    /// on both sides rather than doing a raw <c>systemId == viewerSystemId</c> byte compare.
    /// Pre-Round-4 the Tag and Fronting copies did the byte compare, which silently missed
    /// the self-branch when the viewer arrived scoped (<c>"nam:abcdefg"</c>) against an owner
    /// arriving raw (<c>"abcdefg"</c>) or vice versa — the same shape drift Rounds 3–4 fixed
    /// across the account read/write paths. StripRegionPrefix on both sides makes this self-
    /// check byte-identical to the Alter reference and to the Scylla shared query.
    /// </para>
    /// </summary>
    public static async Task<FriendshipLevel?> ResolveFriendshipLevelAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        IFriendshipRepository? friendships,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(viewerSystemId?.Value))
        {
            return null;
        }

        if (SystemIdNormalization.StripRegionPrefix(systemId) ==
            SystemIdNormalization.StripRegionPrefix(viewerSystemId.Value))
        {
            return FriendshipLevel.TrustedFriend;
        }

        if (friendships is null)
        {
            return null;
        }

        return await friendships.GetFriendshipLevelAsync(systemId, viewerSystemId, cancellationToken);
    }
}
