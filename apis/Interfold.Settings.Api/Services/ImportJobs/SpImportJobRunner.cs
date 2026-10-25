using System.Text.Json;
using Interfold.Settings.Contracts.Ids;
using Interfold.Settings.Contracts.Models.Commands;
using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Settings.Domain.Abstractions;
using Interfold.Settings.Domain.Abstractions.ImportJobs;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Settings.Api.Services.ImportJobs;

/// <summary>
/// Bridges the generic <see cref="IImportJobRunner"/> contract to the concrete Simply
/// Plural importer (<see cref="ISimplyPluralImportService"/>). One per-process registration
/// (singleton). The background worker resolves this instance when it dequeues an item
/// with <see cref="ImportFormat.SimplyPlural"/>.
/// </summary>
public sealed class SpImportJobRunner : IImportJobRunner
{
    private readonly ISimplyPluralImportService _importService;

    public SpImportJobRunner(ISimplyPluralImportService importService)
    {
        _importService = importService;
    }

    public ImportFormat Format => ImportFormat.SimplyPlural;

    public Task<ImportJobOutcome> RunAsync(ImportJobItem item, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Deserialize<SpImportPayload>(item.RawPayload);
        if (payload is null)
        {
            return Task.FromResult(new ImportJobOutcome(false, 0, ImportErrorCode.SpImportFailed, "Failed to deserialize Simply Plural payload."));
        }

        var token = new ImportToken(payload.Token);
        RecoveryCode? recoveryCode = payload.RecoveryCode is { } code ? new RecoveryCode(code) : (RecoveryCode?)null;
        return _importService.ImportAsync(item.SystemId, token, recoveryCode, cancellationToken);
    }
}
