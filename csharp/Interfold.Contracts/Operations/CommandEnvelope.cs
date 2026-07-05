using System.Net;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Operations;

public sealed record CommandEnvelope<TPayload>(
    OperationId OperationId,
    Guid CommandId,
    SystemId PrincipalId,
    IdempotencyKey IdempotencyKey,
    DateTimeOffset? OccurredAt,
    TPayload Payload,
    HttpStatusCode? SuccessStatusCode = null
);
