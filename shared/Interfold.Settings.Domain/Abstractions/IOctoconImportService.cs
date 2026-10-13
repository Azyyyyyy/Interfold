using Interfold.Settings.Domain.Abstractions.ImportJobs;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Settings.Domain.Abstractions;

public interface IOctoconImportService
{
    Task<ImportJobOutcome> ImportAsync(SystemId systemId, string rawPayload, CancellationToken cancellationToken = default);
}
