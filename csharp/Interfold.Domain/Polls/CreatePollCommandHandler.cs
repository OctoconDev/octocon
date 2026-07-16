using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Polls;

public sealed class CreatePollCommandHandler : ICommandHandler<CreatePollCommand, PollCommandResult>
{
    private readonly IPollRepository _pollRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public CreatePollCommandHandler(
        IPollRepository pollRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _pollRepository = pollRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<PollCommandResult>> HandleAsync(
        CommandEnvelope<CreatePollCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Title))
            return RejectInvariant(command, EntityRefs.PollTitleRequired);

        if (command.Payload.Title.Length > 100)
            return RejectInvariant(command, EntityRefs.PollTitleTooLong);

        if (!string.IsNullOrWhiteSpace(command.Payload.Description) && command.Payload.Description.Length > 2000)
            return RejectInvariant(command, EntityRefs.PollDescriptionTooLong);

        // Poll type is enum-typed; JSON deserialization has already rejected unknown wire
        // values by the time we get here, so the previous string whitelist is redundant.

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, EntityRefs.PollCreate);

            var replay = CommandSerialization.Deserialize<PollCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<PollCommandResult>.Success(replay with { Replay = true });
        }

        // Stamp the row with the envelope's OccurredAt rather than honouring whatever
        // InsertedAtUtc the caller put on the payload. The caller (PollsController) sends
        // `default(DateTime)` precisely so the idempotency hash stays stable across retries
        // — see PollsController.Create. The SP import bypasses this handler entirely and
        // sets InsertedAtUtc itself from lastOperationTime, so it isn't affected here.
        // OccurredAt is nullable on the envelope; fall back to UtcNow if missing.
        var insertedAtUtc = (command.OccurredAt ?? DateTimeOffset.UtcNow).UtcDateTime;
        var enrichedPayload = command.Payload with { InsertedAtUtc = insertedAtUtc };
        var pollId = await _pollRepository.CreateAsync(command.PrincipalId, enrichedPayload, cancellationToken);
        if (pollId is null)
            return RejectInvariant(command, EntityRefs.PollCreateFailed);

        var result = new PollCommandResult(command.PrincipalId, pollId.Value, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey,
            payloadHash, CommandSerialization.Hash(resultJson), resultJson, cancellationToken);

        await _eventBus.PublishAsync(new PollCreatedEvent(command.PrincipalId, pollId.Value), cancellationToken);
        return CommandExecutionResult<PollCommandResult>.Success(result);
    }

    private static CommandExecutionResult<PollCommandResult> RejectDuplicate(
        CommandEnvelope<CreatePollCommand> command, EntityRef entityRef) =>
        CommandExecutionResult<PollCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, ResolutionHint.NoRetry));

    private static CommandExecutionResult<PollCommandResult> RejectInvariant(
        CommandEnvelope<CreatePollCommand> command, EntityRef entityRef) =>
        CommandExecutionResult<PollCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, ResolutionHint.ManualMergeRequired));
}
