using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Fronting;

public sealed class DeleteFrontByIdCommandHandler : ICommandHandler<DeleteFrontByIdCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public DeleteFrontByIdCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _frontingRepository = frontingRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<FrontCommandResult>> HandleAsync(
        CommandEnvelope<DeleteFrontByIdCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.FrontId == FrontId.Empty)
            return RejectInvariant(command, EntityRefs.FrontingInvalidFrontId);

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, EntityRefs.FrontingDelete);

            var replay = CommandSerialization.Deserialize<FrontCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<FrontCommandResult>.Success(replay with { Replay = true });
        }

        var existing = await _frontingRepository.GetActiveByFrontIdAsync(command.PrincipalId, command.Payload.FrontId, cancellationToken);
        FrontHistoryReadModel? existingHistory = null;
        if (existing is null)
        {
            existingHistory = await _frontingRepository.GetHistoryEntryByFrontIdAsync(command.PrincipalId, command.Payload.FrontId, cancellationToken);
            if (existingHistory is null)
                return RejectInvariant(command, EntityRefs.FrontingNoFront);
        }

        if (existing is not null)
        {
            var deleted = await _frontingRepository.EndByFrontIdAsync(command.PrincipalId, command.Payload.FrontId, cancellationToken);
            if (!deleted)
                return RejectInvariant(command, EntityRefs.FrontingDeleteFailed);
        }

        var deletedFromHistory = await _frontingRepository.DeleteFrontByIdAsync(command.PrincipalId, command.Payload.FrontId, cancellationToken);
        if (!deletedFromHistory)
            return RejectInvariant(command, EntityRefs.FrontingDeleteFailed);

        var alterId = existing?.Front.AlterId ?? existingHistory!.AlterId;
        var result = new FrontCommandResult(command.PrincipalId, alterId, command.Payload.FrontId, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken);

        if (existing is not null)
            await _eventBus.PublishAsync(new FrontingStateChangedEvent(command.PrincipalId), cancellationToken);
        await _eventBus.PublishAsync(new FrontDeletedEvent(command.PrincipalId, command.Payload.FrontId), cancellationToken);

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FrontCommandResult> RejectDuplicate(
        CommandEnvelope<DeleteFrontByIdCommand> command,
        EntityRef entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, ResolutionHint.NoRetry));

    private static CommandExecutionResult<FrontCommandResult> RejectInvariant(
        CommandEnvelope<DeleteFrontByIdCommand> command,
        EntityRef entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, ResolutionHint.ManualMergeRequired));
}