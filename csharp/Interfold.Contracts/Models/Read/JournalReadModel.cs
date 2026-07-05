using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models.Read;

public sealed record JournalReadModel(
    EntryId Id,
    SystemId UserId,
    string Title,
    string? Content,
    string? Color,
    bool Locked,
    bool Pinned,
    DateTime InsertedAt,
    DateTime UpdatedAt,
    IReadOnlyList<AlterId> Alters
);


public sealed record CreateGlobalJournalRequest(
    string Title,
    long? ExpectedVersion = null
);

public sealed record UpdateGlobalJournalRequest(
    string? Title = null,
    string? Content = null,
    string? Color = null,
    long? ExpectedVersion = null
);

public sealed record DeleteGlobalJournalRequest(
    long? ExpectedVersion = null
);

public sealed record JournalActionRequest(
    long? ExpectedVersion = null
);

public sealed record JournalAlterRequest(
    AlterId? AlterId,
    long? ExpectedVersion = null
);
