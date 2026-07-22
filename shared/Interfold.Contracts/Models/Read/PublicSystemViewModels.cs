using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;

namespace Interfold.Contracts.Models.Read;

/// <summary>Read model for <c>GET /api/systems/{systemId}</c>. Emits
/// <c>{ id, avatar_url, avatar_source, username, description }</c> under snake_case.</summary>
public sealed record PublicSystemReadModel(
    SystemId Id,
    AvatarUrl? AvatarUrl,
    AvatarSource? AvatarSource,
    Username? Username,
    string? Description
) : IAvatarBearing;

// PublicSystemBatchReadModel lives in host/Interfold.Api/Models/ (Phase-3 Friendships migration).
// Namespace preserved as Interfold.Contracts.Models.Read for wire-compat; physical file moved out
// of spine to break the Interfold.Contracts <-> Interfold.Friendships.Contracts cycle after
// FriendshipReadModel extracted.
