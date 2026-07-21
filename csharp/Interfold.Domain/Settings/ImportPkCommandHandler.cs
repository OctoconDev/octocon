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
/// Async dispatcher for PluralKit imports — symmetrical to <see cref="ImportSpCommandHandler"/>.
/// The real PK importer is still a stub (see <see cref="ImportJobs.PkImportJobRunner"/>),
/// but the dispatch shape is identical so when the real importer lands it inherits the
/// per-system mutex, the background-worker lifecycle, and the existing
/// <c>pk_import_complete</c> / <c>pk_import_failed</c> WebSocket contract for free.
/// </summary>
public sealed class ImportPkCommandHandler : ICommandHandler<ImportPkCommand, ImportDispatchCommandResult>
{
    private readonly IImportOperationRepository _operations;
    private readonly IImportJobQueue _queue;

    public ImportPkCommandHandler(IImportOperationRepository operations, IImportJobQueue queue)
    {
        _operations = operations;
        _queue = queue;
    }

    public async Task<CommandExecutionResult<ImportDispatchCommandResult>> HandleAsync(
        CommandEnvelope<ImportPkCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (CommandHandler.RejectIfBlank<ImportDispatchCommandResult>(command.OperationId, command.Payload.Token.Value, EntityRefs.SettingsImportPkInvalid) is { } blankReject)
            return blankReject;

        var claim = await _operations.TryClaimAsync(
            command.PrincipalId,
            ImportOperationKind.PluralKit,
            command.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);

        if (claim.IsNew)
        {
            // PK doesn't have a recovery code — the contract carries a single token only.
            await _queue.EnqueueAsync(
                new ImportJobItem(
                    claim.OperationId,
                    command.PrincipalId,
                    ImportOperationKind.PluralKit,
                    command.Payload.Token,
                    RecoveryCode: null),
                cancellationToken).ConfigureAwait(false);
        }

        var result = new ImportDispatchCommandResult(
            command.PrincipalId,
            claim.OperationId,
            ImportOperationKind.PluralKit,
            Status: claim.IsNew
                ? ImportOperationDispatchStatus.Queued
                : ImportOperationDispatchStatus.Running,
            StartedAt: DateTimeOffset.UtcNow,
            Replay: false);

        return CommandExecutionResult<ImportDispatchCommandResult>.Success(result);
    }
}
