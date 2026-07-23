using System.Collections.Concurrent;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;

namespace Interfold.Infrastructure.Coordination;

/// <summary>Primary-node background service that batches fronting-state-change signals
/// and flushes them every 5 s (mirrors legacy <c>Octocon.Global.FrontNotifier</c>).</summary>
public sealed class FrontNotifierBackgroundService(
    IClusterEventBus eventBus,
    IFrontingRepository frontingRepository,
    IFCMService fcmService,
    ILogger<FrontNotifierBackgroundService> logger)
    : BackgroundService
{
    private readonly ConcurrentDictionary<SystemId, long> _pending = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("FrontNotifierBackgroundService starting (primary node).");

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
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 10_000;

            foreach (var systemId in _pending.Keys)
            {
                if (_pending.TryGetValue(systemId, out var ts) && ts < cutoff &&
                    _pending.TryRemove(systemId, out _))
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

            logger.LogDebug(
                "FrontNotifier flushed: system={SystemId}, alters={AlterCount}", systemId, alterIds.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "FrontNotifier flush failed for system={SystemId}", systemId);
        }
    }
}
