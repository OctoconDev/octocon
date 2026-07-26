using Interfold.Settings.Contracts.Events;
using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Settings.Domain.Abstractions.ImportJobs;
using Interfold.Settings.Domain.Abstractions.Repository;
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
    private readonly IReadOnlyDictionary<ImportOperationKind, IImportJobRunner> _runners;
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
        // Duplicate kinds throw here rather than racing at dispatch time.
        _runners = runners.ToDictionary(r => r.Kind);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[import-worker] Starting. Registered kinds: {Kinds}",
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
                    row.Kind,
                    errorCode: ImportErrorCode.HostRestart,
                    errorMessage: "Operation was running when the previous host shut down.",
                    cancellationToken).ConfigureAwait(false);

                // Skip the client event on a legacy unscoped row (the slot still frees).
                if (ScopedSystemId.TryParseScoped(row.SystemId, out var scopedSweepId))
                {
                    await PublishFailureAsync(scopedSweepId, row.Kind, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogWarning(
                        "[import-worker] Skipping sweep failure event for operation {OperationId} — stored system id {SystemId} is not region-scoped.",
                        row.OperationId, row.SystemId);
                }
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
        if (!_runners.TryGetValue(item.Kind, out var runner))
        {
            // Guard against a misregistered DI graph so the row terminates instead of
            // stranding in "running".
            _logger.LogError("[import-worker] No runner registered for kind={Kind} (operation={OperationId}).",
                item.Kind, item.OperationId);
            await _operations.MarkFailedAsync(
                item.SystemId, item.OperationId, item.Kind,
                errorCode: ImportErrorCode.NoRunner,
                errorMessage: $"No IImportJobRunner is registered for kind '{item.Kind}'.",
                cancellationToken).ConfigureAwait(false);
            await PublishFailureAsync(item.SystemId, item.Kind, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _operations.MarkRunningAsync(item.SystemId, item.OperationId, cancellationToken).ConfigureAwait(false);

        try
        {
            var outcome = await runner.RunAsync(item, cancellationToken).ConfigureAwait(false);
            if (outcome.Success)
            {
                await _operations.MarkSucceededAsync(
                    item.SystemId, item.OperationId, item.Kind, outcome.AlterCount, cancellationToken)
                    .ConfigureAwait(false);

                // Pin the legacy settings-profile-updated signal so encryption-status views refresh.
                if (item.Kind == ImportOperationKind.SimplyPlural)
                {
                    await _eventBus.PublishAsync(
                        new SettingsProfileUpdatedEvent(item.SystemId, EmitUsernameUpdated: false),
                        cancellationToken).ConfigureAwait(false);
                }

                await PublishSuccessAsync(item.SystemId, item.Kind, outcome.AlterCount, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("[import-worker] Operation {OperationId} succeeded ({AlterCount} alters, kind={Kind}).",
                    item.OperationId, outcome.AlterCount, item.Kind);
            }
            else
            {
                await _operations.MarkFailedAsync(
                    item.SystemId, item.OperationId, item.Kind,
                    outcome.ErrorCode ?? ImportErrorCode.ImportFailed,
                    outcome.ErrorMessage,
                    cancellationToken).ConfigureAwait(false);

                await PublishFailureAsync(item.SystemId, item.Kind, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("[import-worker] Operation {OperationId} failed (kind={Kind}, code={ErrorCode}).",
                    item.OperationId, item.Kind, outcome.ErrorCode);
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
            _logger.LogError(ex, "[import-worker] Operation {OperationId} threw (kind={Kind}).", item.OperationId, item.Kind);
            await TryMarkFailedSafelyAsync(item, ImportErrorCode.Exception, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task TryMarkFailedSafelyAsync(ImportJobItem item, ImportErrorCode errorCode, string errorMessage)
    {
        // CancellationToken.None so a host cancel still lands the terminal write and frees the slot.
        try
        {
            await _operations.MarkFailedAsync(
                item.SystemId, item.OperationId, item.Kind, errorCode, errorMessage, CancellationToken.None)
                .ConfigureAwait(false);
            await PublishFailureAsync(item.SystemId, item.Kind, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[import-worker] Failed to record terminal failure for operation {OperationId}; slot may remain held until sweep.",
                item.OperationId);
        }
    }

    private ValueTask PublishSuccessAsync(ScopedSystemId systemId, ImportOperationKind kind, int alterCount, CancellationToken cancellationToken)
    {
        return kind switch
        {
            ImportOperationKind.SimplyPlural =>
                _eventBus.PublishAsync(new SimplyPluralImportCompletedEvent(systemId, alterCount), cancellationToken),
            ImportOperationKind.PluralKit =>
                _eventBus.PublishAsync(new PluralKitImportCompletedEvent(systemId, alterCount), cancellationToken),
            _ => ValueTask.CompletedTask,
        };
    }

    private ValueTask PublishFailureAsync(ScopedSystemId systemId, ImportOperationKind kind, CancellationToken cancellationToken)
    {
        return kind switch
        {
            ImportOperationKind.SimplyPlural =>
                _eventBus.PublishAsync(new SimplyPluralImportFailedEvent(systemId), cancellationToken),
            ImportOperationKind.PluralKit =>
                _eventBus.PublishAsync(new PluralKitImportFailedEvent(systemId), cancellationToken),
            _ => ValueTask.CompletedTask,
        };
    }
}
