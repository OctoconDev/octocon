using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Fronting;

public sealed class SetPrimaryFrontCommandHandler : ICommandHandler<SetPrimaryFrontCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public SetPrimaryFrontCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus
    )
    {
        _frontingRepository = frontingRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<FrontCommandResult>> HandleAsync(
        CommandEnvelope<SetPrimaryFrontCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (command.Payload.AlterId?.Value is < 1 or > 32_767)
        {
            return RejectInvariant(command, EntityRefs.FrontingInvalidAlterId);
        }

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
            {
                return RejectDuplicate(command, EntityRefs.FrontingPrimary);
            }

            var replay = CommandSerialization.Deserialize<FrontCommandResult>(previous.OutcomePayload);
            if (replay is not null)
            {
                return CommandExecutionResult<FrontCommandResult>.Success(replay with { Replay = true });
            }
        }

        if (command.Payload.AlterId is { } alterId)
        {
            var fronting = await _frontingRepository.IsFrontingAsync(command.PrincipalId, alterId, cancellationToken);
            if (!fronting)
            {
                return RejectInvariant(command, EntityRefs.FrontingNotFronting);
            }
        }

        var set = await _frontingRepository.SetPrimaryAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (!set)
        {
            return RejectInvariant(command, EntityRefs.FrontingPrimaryFailed);
        }

        var result = new FrontCommandResult(command.PrincipalId, command.Payload.AlterId, FrontId: null, Replay: false);
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

        await _eventBus.PublishAsync(new FrontingStateChangedEvent(command.PrincipalId), cancellationToken);
        await _eventBus.PublishAsync(new FrontingPrimaryChangedEvent(command.PrincipalId, command.Payload.AlterId), cancellationToken);
        await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, false), cancellationToken);

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FrontCommandResult> RejectDuplicate(
        CommandEnvelope<SetPrimaryFrontCommand> command,
        EntityRef entityRef
    ) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictDuplicate,
                command.OperationId,
                entityRef,
                ResolutionHint.NoRetry
            )
        );

    private static CommandExecutionResult<FrontCommandResult> RejectInvariant(
        CommandEnvelope<SetPrimaryFrontCommand> command,
        EntityRef entityRef
    ) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictInvariant,
                command.OperationId,
                entityRef,
                ResolutionHint.ManualMergeRequired
            )
        );
}