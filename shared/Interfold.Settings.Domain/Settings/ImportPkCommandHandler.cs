using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Models.ImportOperations;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.ImportJobs;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Settings;

/// <summary>Async dispatcher for PK imports — symmetrical to
/// <see cref="ImportSpCommandHandler"/>. The real PK importer is still a stub, but the
/// dispatch shape mirrors SP so the real importer inherits the mutex + lifecycle for free.</summary>
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
