using Interfold.Settings.Contracts.Events;
using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Settings.Domain.Abstractions.ImportJobs;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Settings.Api.Services.ImportJobs;

/// <summary>Single-consumer worker: drains <see cref="IImportJobQueue"/>, transitions
/// <c>import_operations</c> rows, and publishes completion/failure events for the WS pump.
/// On startup, sweeps rows stuck in <see cref="ImportOperationStatus.Running"/> older than
/// <see cref="StaleRunningThreshold"/> (previous-host crashes) and marks them failed with
/// code <c>host_restart</c>. Per-job try/catch treats escaped exceptions as failed runner
/// outcomes (<c>error_code = "exception"</c>) so the loop survives.</summary>
public sealed class ImportJobBackgroundService : BackgroundService
{
    // Paired with the LWT mutex — every extra minute here delays new imports for the
    // affected system by that much.
    private static readonly TimeSpan StaleRunningThreshold = TimeSpan.FromMinutes(10);

    private readonly IImportJobQueue _queue;
    private readonly IImportOperationRepository _operations;
    private readonly IClusterEventBus _eventBus;
    private readonly IReadOnlyDictionary<ImportFormat, IImportJobRunner> _runners;
    private readonly ILogger<ImportJobBackgroundService> _logger;

    public ImportJobBackgroundService(
        IImportJobQueue queue,
        IImportOperationRepository operations,
        IClusterEventBus eventBus,
        IEnumerable<IImportJobRunner> runners,
        ILogger<ImportJobBackgroundService> logger)
    {
        _queue = queue;
        _operations = operations;
        _eventBus = eventBus;
        // Duplicate formats throw here rather than racing at dispatch time.
        _runners = runners.ToDictionary(r => r.Format);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[import-worker] Starting. Registered formats: {Formats}",
            string.Join(", ", _runners.Keys));

        await SweepStaleRunningAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            await foreach (var item in _queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ProcessOneAsync(item, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown. Any queued work is rediscovered by the next sweep.
        }

        _logger.LogInformation("[import-worker] Stopped.");
    }

    private async Task SweepStaleRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stale = await _operations.GetStaleRunningAsync(StaleRunningThreshold, cancellationToken).ConfigureAwait(false);
            if (stale.Count == 0)
            {
                return;
            }

            _logger.LogWarning("[import-worker] Startup sweep found {Count} stale running operation(s); marking failed.", stale.Count);

            foreach (var row in stale)
            {
                await _operations.MarkFailedAsync(
                    row.SystemId,
                    row.OperationId,
                    row.Format,
                    errorCode: ImportErrorCode.HostRestart,
                    errorMessage: "Operation was running when the previous host shut down.",
                    cancellationToken).ConfigureAwait(false);

                await PublishFailureAsync(row.SystemId, row.Format, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Must not block new work — a stuck sweep would pin the loop forever.
            _logger.LogError(ex, "[import-worker] Startup sweep failed; continuing without it.");
        }
    }

    private async Task ProcessOneAsync(ImportJobItem item, CancellationToken cancellationToken)
    {
        if (!_runners.TryGetValue(item.Format, out var runner))
        {
            // Guard against a misregistered DI graph so the row terminates instead of
            // stranding in "running".
            _logger.LogError("[import-worker] No runner registered for format={Format} (operation={OperationId}).",
                item.Format, item.OperationId);
            await _operations.MarkFailedAsync(
                item.SystemId, item.OperationId, item.Format,
                errorCode: ImportErrorCode.NoRunner,
                errorMessage: $"No IImportJobRunner is registered for format '{item.Format}'.",
                cancellationToken).ConfigureAwait(false);
            await PublishFailureAsync(item.SystemId, item.Format, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _operations.MarkRunningAsync(item.SystemId, item.OperationId, cancellationToken).ConfigureAwait(false);

        try
        {
            var outcome = await runner.RunAsync(item, cancellationToken).ConfigureAwait(false);
            if (outcome.Success)
            {
                await _operations.MarkSucceededAsync(
                    item.SystemId, item.OperationId, item.Format, outcome.AlterCount, cancellationToken)
                    .ConfigureAwait(false);

                // Pin the legacy settings-profile-updated signal so encryption-status views refresh.
                if (item.Format == ImportFormat.SimplyPlural)
                {
                    await _eventBus.PublishAsync(
                        new SettingsProfileUpdatedEvent(item.SystemId, EmitUsernameUpdated: false),
                        cancellationToken).ConfigureAwait(false);
                }

                await PublishSuccessAsync(item.SystemId, item.Format, outcome.AlterCount, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("[import-worker] Operation {OperationId} succeeded ({AlterCount} alters, format={Format}).",
                    item.OperationId, outcome.AlterCount, item.Format);
            }
            else
            {
                await _operations.MarkFailedAsync(
                    item.SystemId, item.OperationId, item.Format,
                    outcome.ErrorCode ?? ImportErrorCode.ImportFailed,
                    outcome.ErrorMessage,
                    cancellationToken).ConfigureAwait(false);

                await PublishFailureAsync(item.SystemId, item.Format, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("[import-worker] Operation {OperationId} failed (format={Format}, code={ErrorCode}).",
                    item.OperationId, item.Format, outcome.ErrorCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Terminate the row so the client sees a definitive frame; the next sweep won't double-process.
            await TryMarkFailedSafelyAsync(item, ImportErrorCode.HostShutdown, "Worker was cancelled mid-job.").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[import-worker] Operation {OperationId} threw (format={Format}).", item.OperationId, item.Format);
            await TryMarkFailedSafelyAsync(item, ImportErrorCode.Exception, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task TryMarkFailedSafelyAsync(ImportJobItem item, ImportErrorCode errorCode, string errorMessage)
    {
        // CancellationToken.None so a host cancel still lands the terminal write and frees the slot.
        try
        {
            await _operations.MarkFailedAsync(
                item.SystemId, item.OperationId, item.Format, errorCode, errorMessage, CancellationToken.None)
                .ConfigureAwait(false);
            await PublishFailureAsync(item.SystemId, item.Format, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[import-worker] Failed to record terminal failure for operation {OperationId}; slot may remain held until sweep.",
                item.OperationId);
        }
    }

    private ValueTask PublishSuccessAsync(SystemId systemId, ImportFormat format, int alterCount, CancellationToken cancellationToken)
    {
        return format switch
        {
            ImportFormat.SimplyPlural =>
                _eventBus.PublishAsync(new SimplyPluralImportCompletedEvent(systemId, alterCount), cancellationToken),
            ImportFormat.PluralKit =>
                _eventBus.PublishAsync(new PluralKitImportCompletedEvent(systemId, alterCount), cancellationToken),
            ImportFormat.Octocon =>
                _eventBus.PublishAsync(new OctoconImportCompletedEvent(systemId, alterCount), cancellationToken),
            _ => ValueTask.CompletedTask,
        };
    }

    private ValueTask PublishFailureAsync(SystemId systemId, ImportFormat format, CancellationToken cancellationToken)
    {
        return format switch
        {
            ImportFormat.SimplyPlural =>
                _eventBus.PublishAsync(new SimplyPluralImportFailedEvent(systemId), cancellationToken),
            ImportFormat.PluralKit =>
                _eventBus.PublishAsync(new PluralKitImportFailedEvent(systemId), cancellationToken),
            ImportFormat.Octocon =>
                _eventBus.PublishAsync(new OctoconImportFailedEvent(systemId), cancellationToken),
            _ => ValueTask.CompletedTask,
        };
    }
}
