using System.Collections.Concurrent;
using Interfold.Fronting.Contracts.Events;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Coordination;

/// <summary>Primary-node background service that batches fronting-state-change signals
/// and flushes them on <see cref="FrontNotifierConfiguration.FlushMs"/> (production default 5 s).
/// A system is flushed only after it has been quiet for
/// <see cref="FrontNotifierConfiguration.QuietMs"/> (production default 10 s).</summary>
public sealed class FrontNotifierBackgroundService(
    IClusterEventBus eventBus,
    IFrontingRepository frontingRepository,
    IFCMService fcmService,
    IOptions<FrontNotifierConfiguration> options,
    ILogger<FrontNotifierBackgroundService> logger)
    : BackgroundService
{
    private readonly ConcurrentDictionary<SystemId, long> _pending = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = options.Value;
        logger.LogInformation(
            "FrontNotifierBackgroundService starting. quiet_ms={QuietMs} flush_ms={FlushMs}",
            cfg.QuietMs, cfg.FlushMs);

        var subscription = ConsumeEventsAsync(stoppingToken);
        var flush = FlushLoopAsync(stoppingToken);

        await Task.WhenAll(subscription, flush).ConfigureAwait(false);

        logger.LogInformation("FrontNotifierBackgroundService stopped.");
    }

    private async Task ConsumeEventsAsync(CancellationToken ct)
    {
        await foreach (var evt in eventBus.SubscribeAsync<FrontingStateChangedEvent>(ct).ConfigureAwait(false))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _pending.AddOrUpdate(evt.TargetSystemId, now, (_, _) => now);
        }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var flushMs = Math.Max(50, options.Value.FlushMs);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(flushMs));

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var quietMs = Math.Max(0, options.Value.QuietMs);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var cutoff = now - quietMs;

            foreach (var systemId in _pending.Keys)
            {
                if (!_pending.TryGetValue(systemId, out var ts))
                    continue;

                var due = quietMs == 0 || ts <= cutoff;
                if (due && _pending.TryRemove(systemId, out _))
                {
                    await FlushSystemAsync(systemId, ct).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task FlushSystemAsync(SystemId systemId, CancellationToken ct)
    {
        try
        {
            var active = await frontingRepository.ListActiveAsync(systemId, ct).ConfigureAwait(false);
            var alterIds = active.Select(f => f.Alter.Id).ToList();

            await fcmService.NotifyFrontingChangedAsync(systemId, alterIds, ct).ConfigureAwait(false);

            logger.LogInformation(
                "FrontNotifier flushed: system={SystemId}, alters={AlterCount}", systemId, alterIds.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "FrontNotifier flush failed for system={SystemId}", systemId);
        }
    }
}
