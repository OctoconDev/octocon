using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Tags;

public sealed class CreateTagCommandHandler : ICommandHandler<CreateTagCommand, TagCommandResult>
{
    private readonly ITagRepository _tagRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public CreateTagCommandHandler(
        ITagRepository tagRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus
    )
    {
        _tagRepository = tagRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<TagCommandResult>> HandleAsync(
        CommandEnvelope<CreateTagCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Name))
            return RejectInvariant(command, EntityRefs.TagNameRequired);

        if (command.Payload.Name.Length > 50)
            return RejectInvariant(command, EntityRefs.TagNameTooLong);

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
                return RejectDuplicate(command, EntityRefs.TagCreate);

            var replay = CommandSerialization.Deserialize<TagCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<TagCommandResult>.Success(replay with { Replay = true });
        }

        if (command.Payload.ParentTagId is { } parentTagId && parentTagId != TagId.Empty)
        {
            var parentExists = await _tagRepository.ExistsAsync(
                command.PrincipalId,
                parentTagId,
                cancellationToken
            );

            if (!parentExists)
                return RejectInvariant(command, EntityRefs.TagParentNotFound);
        }

        // See CreateTagCommand XML-doc: public-API callers send `default(DateTime)` so the
        // idempotency hash stays stable across retries. We stamp the real value here from
        // the envelope just before the repo call. The SP import bypasses this handler and
        // sets InsertedAtUtc itself from the decoded ObjectId, so it isn't affected here.
        var insertedAtUtc = (command.OccurredAt ?? DateTimeOffset.UtcNow).UtcDateTime;
        var tagId = await _tagRepository.CreateAsync(
            command.PrincipalId,
            command.Payload with { InsertedAtUtc = insertedAtUtc },
            cancellationToken);
        if (tagId is null)
            return RejectInvariant(command, EntityRefs.TagCreateFailed);

        var result = new TagCommandResult(command.PrincipalId, tagId.Value, Replay: false);
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
            new TagCreatedEvent(command.PrincipalId, tagId.Value),
            cancellationToken);

        return CommandExecutionResult<TagCommandResult>.Success(result);
    }

    private static CommandExecutionResult<TagCommandResult> RejectDuplicate(
        CommandEnvelope<CreateTagCommand> command,
        EntityRef entityRef
    ) =>
        CommandExecutionResult<TagCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, ResolutionHint.NoRetry)
        );

    private static CommandExecutionResult<TagCommandResult> RejectInvariant(
        CommandEnvelope<CreateTagCommand> command,
        EntityRef entityRef
    ) =>
        CommandExecutionResult<TagCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, ResolutionHint.ManualMergeRequired)
        );
}
