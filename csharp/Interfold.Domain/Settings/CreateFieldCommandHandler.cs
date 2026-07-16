using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Settings;

public sealed class CreateFieldCommandHandler : ICommandHandler<CreateFieldCommand, SettingsFieldCommandResult>
{
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public CreateFieldCommandHandler(ISettingsFieldRepository fieldRepository, IIdempotencyStore idempotencyStore, IClusterEventBus eventBus)
    {
        _fieldRepository = fieldRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsFieldCommandResult>> HandleAsync(CommandEnvelope<CreateFieldCommand> command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Name))
        {
            return CommandExecutionResult<SettingsFieldCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, EntityRefs.SettingsFieldNameRequired, ResolutionHint.ManualMergeRequired));
        }

        // Type and SecurityLevel are enum-typed on the command, so JSON deserialization
        // has already rejected any unknown wire values before this handler runs. The
        // previous string whitelists lived here to catch that; the type system now owns
        // it, and we keep the fallback-to-Text behaviour from the pre-enum NormalizeType.
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
            {
                return CommandExecutionResult<SettingsFieldCommandResult>.Rejected(
                    new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, EntityRefs.SettingsFieldCreate, ResolutionHint.NoRetry));
            }

            var replay = CommandSerialization.Deserialize<SettingsFieldCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<SettingsFieldCommandResult>.Success(replay with { Replay = true });
        }

        // Stamp the row with the envelope's OccurredAt rather than honouring whatever
        // InsertedAtUtc the caller put on the payload. The caller (SettingsController) sends
        // `default(DateTime)` precisely so the idempotency hash stays stable across retries
        // - see SettingsController.CreateField. The SP import bypasses this handler entirely
        // and sets InsertedAtUtc itself from the decoded ObjectId, so it isn't affected here.
        // OccurredAt is nullable on the envelope; fall back to UtcNow if missing.
        var insertedAtUtc = (command.OccurredAt ?? DateTimeOffset.UtcNow).UtcDateTime;

        var fieldId = await _fieldRepository.CreateAsync(
            command.PrincipalId,
            command.Payload.Name,
            command.Payload.Type,
            command.Payload.SecurityLevel,
            command.Payload.Locked,
            insertedAtUtc,
            cancellationToken);

        if (fieldId is null)
        {
            return CommandExecutionResult<SettingsFieldCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, EntityRefs.SettingsFieldCreateFailed, ResolutionHint.ManualMergeRequired));
        }

        var result = new SettingsFieldCommandResult(command.PrincipalId, SettingsFieldAction.FieldCreated, fieldId.Value, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken);

        if (!result.Replay)
            await _eventBus.PublishAsync(new SettingsFieldsChangedEvent(command.PrincipalId), cancellationToken);

        return CommandExecutionResult<SettingsFieldCommandResult>.Success(result);
    }
}