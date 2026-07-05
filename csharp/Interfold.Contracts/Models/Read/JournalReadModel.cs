using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models.Read;

public sealed record JournalReadModel(
    EntryId Id,
    SystemId UserId,
    string Title,
    string? Content,
    HexColor? Color,
    bool Locked,
    bool Pinned,
    DateTime InsertedAt,
    DateTime UpdatedAt,
    IReadOnlyList<AlterId> Alters
);


public sealed record CreateGlobalJournalRequest(
    string Title,
    EntityVersion? ExpectedVersion = null
);

public sealed record UpdateGlobalJournalRequest(
    string? Title = null,
    string? Content = null,
    HexColor? Color = null,
    EntityVersion? ExpectedVersion = null
);

public sealed record DeleteGlobalJournalRequest(
    EntityVersion? ExpectedVersion = null
);

public sealed record JournalActionRequest(
    EntityVersion? ExpectedVersion = null
);

public sealed record JournalAlterRequest(
    AlterId? AlterId,
    EntityVersion? ExpectedVersion = null
);
