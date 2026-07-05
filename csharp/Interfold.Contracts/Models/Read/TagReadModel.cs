using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models.Read;

public sealed record TagReadModel(
    TagId Id,
    string Name,
    HexColor? Color,
    string? Description,
    TagId? ParentTagId,
    IReadOnlyList<AlterId> Alters,
    DateTime InsertedAt,
    DateTime UpdatedAt,
    VisibilityLevel SecurityLevel,
    SystemId? UserId
);

public sealed record TagPublicReadModel(
    TagId Id,
    string Name,
    HexColor? Color,
    string? Description,
    TagId? ParentTagId,
    IReadOnlyList<BareAlter> Alters,
    DateTime InsertedAt,
    DateTime UpdatedAt,
    VisibilityLevel SecurityLevel,
    SystemId? UserId
);

public sealed record CreateTagRequest(
    string Name,
    TagId? ParentTagId
);

public sealed record UpdateTagRequest(
    string? Name = null,
    HexColor? Color = null,
    string? Description = null,
    VisibilityLevel? SecurityLevel = null
);

public sealed record TagAlterRequest(
    AlterId? AlterId
);

public sealed record SetParentRequest(
    TagId? ParentTagId
);
