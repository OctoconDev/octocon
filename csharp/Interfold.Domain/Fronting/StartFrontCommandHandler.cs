using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Fronting;

public sealed class StartFrontCommandHandler : ICommandHandler<StartFrontCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public StartFrontCommandHandler(
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
        CommandEnvelope<StartFrontCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (command.Payload.AlterId.Value is < 1 or > 32_767)
        {
            return RejectInvariant(command, EntityRefs.FrontingInvalidAlterId);
        }

        if ((command.Payload.Comment?.Length ?? 0) > 50)
        {
            return RejectInvariant(command, EntityRefs.FrontingInvalidComment);
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
                return RejectDuplicate(command, EntityRefs.FrontingStart);
            }

            var replay = CommandSerialization.Deserialize<FrontCommandResult>(previous.OutcomePayload);
            if (replay is not null)
            {
                return CommandExecutionResult<FrontCommandResult>.Success(replay with { Replay = true });
            }
        }

        var alreadyFronting = await _frontingRepository.IsFrontingAsync(
            command.PrincipalId,
            command.Payload.AlterId,
            cancellationToken
        );

        if (alreadyFronting)
        {
            return RejectInvariant(command, EntityRefs.FrontingAlreadyFronting);
        }

        var frontId = await _frontingRepository.StartAsync(
            command.PrincipalId,
            command.Payload.AlterId,
            command.Payload.Comment,
            DateTimeOffset.UtcNow,
            cancellationToken
        );

        if (frontId is null)
        {
            return RejectInvariant(command, EntityRefs.FrontingStartFailed);
        }

        var result = new FrontCommandResult(command.PrincipalId, command.Payload.AlterId, frontId, Replay: false);
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

        // Emit granular event for socket layer to handle fronting_started
        await _eventBus.PublishAsync(new FrontingStartedEvent(command.PrincipalId, frontId.Value), cancellationToken);

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FrontCommandResult> RejectDuplicate(
        CommandEnvelope<StartFrontCommand> command,
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
        CommandEnvelope<StartFrontCommand> command,
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