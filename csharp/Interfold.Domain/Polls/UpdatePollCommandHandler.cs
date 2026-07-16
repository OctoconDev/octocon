using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Polls;

public sealed class UpdatePollCommandHandler : ICommandHandler<UpdatePollCommand, PollCommandResult>
{
    private readonly IPollRepository _pollRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public UpdatePollCommandHandler(
        IPollRepository pollRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _pollRepository = pollRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<PollCommandResult>> HandleAsync(
        CommandEnvelope<UpdatePollCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.Title is null && command.Payload.Description is null &&
            !command.Payload.HasTimeEnd && command.Payload.Data is null)
            return RejectInvariant(command, EntityRefs.PollNoFields);

        if (command.Payload.Title is not null && command.Payload.Title.Length > 100)
            return RejectInvariant(command, EntityRefs.PollTitleTooLong);

        if (command.Payload.Description is not null && command.Payload.Description.Length > 2000)
            return RejectInvariant(command, EntityRefs.PollDescriptionTooLong);

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, EntityRefs.PollUpdate);

            var replay = CommandSerialization.Deserialize<PollCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<PollCommandResult>.Success(replay with { Replay = true });
        }

        var exists = await _pollRepository.ExistsAsync(command.PrincipalId, command.Payload.Id, cancellationToken);
        if (!exists)
            return RejectInvariant(command, EntityRefs.PollNotFound);
        
        var updated = await _pollRepository.UpdateAsync(command.PrincipalId, command.Payload, cancellationToken);
        if (!updated)
            return RejectInvariant(command, EntityRefs.PollUpdateFailed);

        var result = new PollCommandResult(command.PrincipalId, command.Payload.Id, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey,
            payloadHash, CommandSerialization.Hash(resultJson), resultJson, cancellationToken);

        await _eventBus.PublishAsync(new PollUpdatedEvent(command.PrincipalId, command.Payload.Id), cancellationToken);
        return CommandExecutionResult<PollCommandResult>.Success(result);
    }

    private static CommandExecutionResult<PollCommandResult> RejectDuplicate(
        CommandEnvelope<UpdatePollCommand> command, EntityRef entityRef) =>
        CommandExecutionResult<PollCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, ResolutionHint.NoRetry));

    private static CommandExecutionResult<PollCommandResult> RejectInvariant(
        CommandEnvelope<UpdatePollCommand> command, EntityRef entityRef) =>
        CommandExecutionResult<PollCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, ResolutionHint.ManualMergeRequired));
}
