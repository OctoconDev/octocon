using Interfold.Contracts.Events;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.ImportOperations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.ImportJobs;
using Interfold.Domain.Abstractions.Repository;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Interfold.Api.Services.ImportJobs;

/// <summary>
/// Single-consumer worker that drains <see cref="IImportJobQueue"/> and drives each
/// import job through its lifecycle. Owns the side-effects that used to live inside the
/// synchronous command handlers — calling <see cref="ISimplyPluralImportService.ImportAsync"/>,
/// transitioning <c>import_operations</c> rows to terminal states, and publishing
/// completion / failure events for the WebSocket pump to relay to the client.
///
/// <para>
/// <b>Startup sweep.</b> Before consuming the queue the worker scans
/// <c>import_operations</c> for any row stuck in <see cref="ImportOperationStatus.Running"/>
/// older than <see cref="StaleRunningThreshold"/>. Those rows belong to a previous
/// process that crashed mid-import; we mark them <see cref="ImportOperationStatus.Failed"/>
/// with code <c>host_restart</c> and publish the matching failure event so any
/// reconnecting client can re-trigger. This is the only path that frees an
/// LWT-protected slot without the owning worker — the
/// <see cref="IImportOperationRepository.MarkFailedAsync"/> contract guards the slot
/// release with <c>IF operation_id = ?</c> so we cannot evict an unrelated newer
/// operation.
/// </para>
///
/// <para>
/// <b>Per-job exception isolation.</b> The runner contract specifies graceful failures
/// are returned, not thrown — but a thrown exception still must not kill the loop. We
/// wrap each iteration in try/catch and treat an escaped exception identically to a
/// failed runner outcome (<c>error_code = "exception"</c>) so the worker stays alive
/// for the next dispatch.
/// </para>
/// </summary>
public sealed class ImportJobBackgroundService : BackgroundService
{
    /// <summary>
    /// How long a <c>running</c> row may stay running before the startup sweep declares
    /// it abandoned and rewrites it to <c>failed</c>. Imports on the slowest known host
    /// (Pi 4 + Cassandra) complete in well under a minute; 10 minutes is comfortable
    /// headroom for any future expansion of scope while still bounding orphan-row lifetime
    /// to "one host restart cycle". This value is paired with the LWT mutex — every
    /// extra minute here is an extra minute the affected system cannot dispatch a new
    /// import.
    /// </summary>
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
        // Materialise runners into a kind-keyed dictionary at startup so the per-job
        // resolve is an O(1) lookup. Duplicate kinds throw at startup rather than racing
        // at dispatch time — better to fail-fast on a misregistered DI graph.
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
            // Normal shutdown path. Any in-flight Cassandra writes for the
            // currently-processing job have already been awaited by ProcessOneAsync;
            // anything still queued will be rediscovered by the next process via the
            // sweep on startup.
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

                // Repositories persist scoped ids on write, so a stale row swept here
                // should be parse-clean. If it isn't (a legacy row that survived migration)
                // we log and skip the client-visible event so a malformed id can't
                // propagate into the topic name — the row is still marked failed above,
                // so the slot frees regardless.
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
            // A sweep failure must not block the worker from accepting new work — a stuck
            // sweep query (e.g. Cassandra under load) would otherwise pin the loop forever.
            _logger.LogError(ex, "[import-worker] Startup sweep failed; continuing without it.");
        }
    }

    private async Task ProcessOneAsync(ImportJobItem item, CancellationToken cancellationToken)
    {
        if (!_runners.TryGetValue(item.Kind, out var runner))
        {
            // Unknown kind shouldn't happen because the controller validates at dispatch
            // time, but a misregistered DI graph plus a runtime push could land here.
            // Fail the operation row so the slot frees and the caller gets a failure
            // frame rather than a perpetual "running" state.
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

                // Pin the legacy "settings profile updated" signal for the SP path so any
                // dependent client view (e.g. encryption status pill) refreshes — same
                // semantics as the pre-async handler's emit-on-accept.
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
            // Host is shutting down mid-job. Mark the operation failed with a stable code
            // so the client receives a definitive frame instead of a never-resolving
            // spinner. The next startup's sweep will not double-process because the row
            // has already terminated here.
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
        // Use CancellationToken.None for the terminal write — if the host token has just
        // fired we still want the operation row to land in a definite state and the slot
        // to free. The Cassandra write itself is bounded by the resilience pipeline.
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
