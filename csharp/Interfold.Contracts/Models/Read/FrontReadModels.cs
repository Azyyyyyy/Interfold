using System.Text.Json.Serialization;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models.Read;

public sealed record FrontActiveReadModel(
    BareAlter Alter,
    FrontHistoryReadModel Front,
    bool Primary
);

public sealed record FrontHistoryReadModel(
    FrontId Id,
    AlterId AlterId,
    string? Comment,
    DateTimeOffset TimeStart,
    DateTimeOffset? TimeEnd,
    SystemId UserId
);


public sealed record FrontBulkUpdateRequest(
    IReadOnlyList<FrontStartEntry> Start,
    IReadOnlyList<AlterId> End
);

public sealed record FrontStartEntry(AlterId AlterId, string? Comment = null);

// Front start/end/set/primary requests take the target alter as `id` — the only wire
// name the Kotlin client (and the replay fixtures) ever send. The former `alter_id`
// alias was exercised only by the server's own tests and has been removed.
public sealed record FrontStartRequest(
    [property: JsonPropertyName("id")] AlterId? Id = null,
    string? Comment = null
);

public sealed record FrontEndRequest(
    [property: JsonPropertyName("id")] AlterId? Id = null
);

public sealed record FrontPrimaryRequest(
    [property: JsonPropertyName("id")] AlterId? Id = null
);

public sealed record FrontSetRequest(
    [property: JsonPropertyName("id")] AlterId? Id = null,
    string? Comment = null
);

public sealed record FrontCommentRequest(
    string Comment
);

public sealed record FrontStartedResponse(FrontId FrontId);

