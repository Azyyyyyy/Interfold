namespace Interfold.Contracts.Models.Read;

/// <summary>Read model for <c>GET /api/systems/{systemId}/batch</c>. Emits
/// <c>{ friendship, tags, alters }</c>; guarded (visibility-filtered) projections
/// use <see cref="TagPublicReadModel"/> and <see cref="BareAlter"/>. Namespace
/// preserved for wire-compat; physical file moved out of Interfold.Contracts
/// during the Phase-3 Friendships migration to break the spine-batch-DTO cycle
/// with Interfold.Friendships.Contracts. Migrates into the Public Systems
/// facade (Phase 3 #8).</summary>
public sealed record PublicSystemBatchReadModel(
    FriendshipReadModel? Friendship,
    IReadOnlyList<TagPublicReadModel> Tags,
    IReadOnlyList<BareAlter> Alters
);
