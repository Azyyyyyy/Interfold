using Interfold.Settings.Contracts.Ids;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Settings.Domain.Abstractions.ImportJobs;

/// <summary>A unit of work for the async import worker. Handler → in-process queue →
/// runner; carries the raw request payload (tokens serialized for third-party imports,
/// raw JSON string for direct imports).</summary>
/// <param name="RawPayload">Raw import payload (sensitive for token-bearing imports — never log).</param>
public sealed record ImportJobItem(
    ImportOperationId OperationId,
    SystemId SystemId,
    ImportFormat Format,
    string RawPayload);
