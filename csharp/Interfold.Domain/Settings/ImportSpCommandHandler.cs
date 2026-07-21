using Interfold.Contracts;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.ImportOperations;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.ImportJobs;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Settings;

/// <summary>
/// Async dispatcher for Simply Plural imports. The handler no longer runs the importer
/// itself — it only claims a per-system slot (LWT-protected) and enqueues the actual
/// work for the <c>ImportJobBackgroundService</c> to pick up. Returns in a few ms so the
/// controller can respond with HTTP 202 Accepted and the client UI flips into its local
/// "importing" state immediately (no Polly-induced retry storm because there's nothing
/// for the loopback HttpClient to time out on).
///
/// <para>
/// <b>Why the old code path is gone.</b> Synchronously running the importer inside a
/// per-command idempotency helper fired the dedupe on the request-level key only. The
/// duplicate-SP-import bug bypassed that dedupe by minting a fresh idempotency key per
/// Polly retry (see <c>GetIdempotencyKey</c>'s fallback). Replacing the dedupe with the
/// per-system LWT mutex (<c>active_import_by_system</c>) means every dispatch — original
/// or retried, from any client — collapses onto the same in-flight operation, regardless
/// of what the idempotency key looks like.
/// </para>
/// </summary>
public sealed class ImportSpCommandHandler : ICommandHandler<ImportSpCommand, ImportDispatchCommandResult>
{
    private readonly IImportOperationRepository _operations;
    private readonly IImportJobQueue _queue;

    public ImportSpCommandHandler(IImportOperationRepository operations, IImportJobQueue queue)
    {
        _operations = operations;
        _queue = queue;
    }

    public async Task<CommandExecutionResult<ImportDispatchCommandResult>> HandleAsync(
        CommandEnvelope<ImportSpCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (CommandHandler.RejectIfBlank<ImportDispatchCommandResult>(command.OperationId, command.Payload.Token.Value, EntityRefs.SettingsImportSpInvalid) is { } blankReject)
            return blankReject;

        var claim = await _operations.TryClaimAsync(
            command.PrincipalId,
            ImportOperationKind.SimplyPlural,
            command.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);

        if (claim.IsNew)
        {
            await _queue.EnqueueAsync(
                new ImportJobItem(
                    claim.OperationId,
                    command.PrincipalId,
                    ImportOperationKind.SimplyPlural,
                    command.Payload.Token,
                    command.Payload.RecoveryCode),
                cancellationToken).ConfigureAwait(false);
        }

        // The "running" status returned on a collapse is intentional: a second dispatch
        // for the same system while a job is still in flight is a NO-OP at the worker
        // layer (we never enqueue), but the caller still gets back a real operation_id
        // so any client log / debugger sees the same correlation handle as the
        // originating click. Replay is wired to false because async dispatch doesn't have
        // the "exact same result available on replay" semantics the old synchronous
        // helper gave — the WebSocket frame is the authoritative outcome.
        var result = new ImportDispatchCommandResult(
            command.PrincipalId,
            claim.OperationId,
            ImportOperationKind.SimplyPlural,
            Status: claim.IsNew
                ? ImportOperationDispatchStatus.Queued
                : ImportOperationDispatchStatus.Running,
            StartedAt: DateTimeOffset.UtcNow,
            Replay: false);

        return CommandExecutionResult<ImportDispatchCommandResult>.Success(result);
    }
}
