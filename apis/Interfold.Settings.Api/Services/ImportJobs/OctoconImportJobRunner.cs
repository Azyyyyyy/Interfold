using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Settings.Domain.Abstractions;
using Interfold.Settings.Domain.Abstractions.ImportJobs;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Settings.Api.Services.ImportJobs;

/// <summary>
/// Bridges the generic <see cref="IImportJobRunner"/> contract to the concrete Octocon
/// backup importer (<see cref="IOctoconImportService"/>). One per-process registration
/// (singleton). The background worker resolves this instance when it dequeues an item
/// with <see cref="ImportFormat.Octocon"/>.
/// </summary>
public sealed class OctoconImportJobRunner : IImportJobRunner
{
    private readonly IOctoconImportService _importService;

    public OctoconImportJobRunner(IOctoconImportService importService)
    {
        _importService = importService;
    }

    public ImportFormat Format => ImportFormat.Octocon;

    public Task<ImportJobOutcome> RunAsync(ImportJobItem item, CancellationToken cancellationToken = default)
        => _importService.ImportAsync(item.SystemId, item.RawPayload, cancellationToken);
}
