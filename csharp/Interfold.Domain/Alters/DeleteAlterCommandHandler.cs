using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Alters;

public sealed class DeleteAlterCommandHandler : ICommandHandler<DeleteAlterCommand, AlterCommandResult>
{
    private readonly IAlterRepository _alterRepository;
    private readonly IJournalRepository _journalRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public DeleteAlterCommandHandler(
        IAlterRepository alterRepository,
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _alterRepository = alterRepository;
        _journalRepository = journalRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<AlterCommandResult>> HandleAsync(
        CommandEnvelope<DeleteAlterCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.AlterId.Value is < 1 or > 32_767)
            return RejectInvariant(command, EntityRefs.AlterId);

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            cancellationToken
        );

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, EntityRefs.AlterDelete);

            var replay = CommandSerialization.Deserialize<AlterCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<AlterCommandResult>.Success(replay with { Replay = true });
        }

        var exists = await _alterRepository.ExistsAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (!exists)
            return RejectInvariant(command, EntityRefs.AlterNotFound);

        // Cascade BEFORE the alter row itself is removed so a journal-cleanup failure
        // leaves the alter intact (caller can retry); the inverse order would orphan the
        // journals if the alter delete succeeded but cleanup later threw. DeleteAllForAlterAsync
        // also detaches the alter from any global journals it was attached to without
        // deleting the global journal itself (multiple alters can share a group journal).
        await _journalRepository.DeleteAllForAlterAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);

        var deleted = await _alterRepository.DeleteAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (!deleted)
            return RejectInvariant(command, EntityRefs.AlterDeleteFailed);

        //TODO: Delete alter image if it exists

        var result = new AlterCommandResult(command.PrincipalId, command.Payload.AlterId, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken
        );

        await _eventBus.PublishAsync(
            new AlterDeletedEvent(command.PrincipalId, command.Payload.AlterId),
            cancellationToken);

        return CommandExecutionResult<AlterCommandResult>.Success(result);
    }

    private static CommandExecutionResult<AlterCommandResult> RejectDuplicate(
        CommandEnvelope<DeleteAlterCommand> command,
        EntityRef entityRef
    ) =>
        CommandExecutionResult<AlterCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictDuplicate,
                command.OperationId,
                entityRef,
                ResolutionHint.NoRetry
            )
        );

    private static CommandExecutionResult<AlterCommandResult> RejectInvariant(
        CommandEnvelope<DeleteAlterCommand> command,
        EntityRef entityRef
    ) =>
        CommandExecutionResult<AlterCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictInvariant,
                command.OperationId,
                entityRef,
                ResolutionHint.ManualMergeRequired
            )
        );
}
