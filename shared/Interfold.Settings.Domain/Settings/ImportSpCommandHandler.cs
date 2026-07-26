using Interfold.Settings.Contracts;
using Interfold.Settings.Contracts.Models.Commands;
using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Settings.Domain.Abstractions.ImportJobs;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Settings.Domain.Settings;

/// <summary>Async dispatcher for Simply Plural imports. Claims a per-system slot via LWT
/// and enqueues the actual work for <c>ImportJobBackgroundService</c>; the controller
/// returns HTTP 202. Every dispatch — original or retried — collapses onto the same
/// in-flight operation via <c>active_import_by_system</c>.</summary>
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

        // Collapsed dispatch returns Running + the existing operation_id so client logs
        // stay coherent. Replay is false — the WebSocket completion frame is authoritative.
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
