using Interfold.Fronting.Contracts.Ids;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Fronting.Contracts.Models.Commands;

// A null EndedAt is an open front (current_fronts and history). A value is history only.
public sealed record ImportFrontWrite(
    AlterId AlterId,
    string? Comment,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

public sealed record StartFrontCommand(AlterId AlterId, string? Comment);

public sealed record EndFrontCommand(AlterId AlterId);

public sealed record SetPrimaryFrontCommand(AlterId? AlterId);

public sealed record FrontStartItem(AlterId AlterId, string? Comment);

public sealed record BulkUpdateFrontCommand(IReadOnlyList<FrontStartItem> Start, IReadOnlyList<AlterId> End);

public sealed record SetFrontCommand(AlterId AlterId, string? Comment);

public sealed record DeleteFrontByIdCommand(FrontId FrontId);

public sealed record UpdateFrontCommentCommand(FrontId FrontId, string Comment);
