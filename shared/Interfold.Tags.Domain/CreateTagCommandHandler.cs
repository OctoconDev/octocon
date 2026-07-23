using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Tags;

public sealed class CreateTagCommandHandler : IdempotentCommandHandler<CreateTagCommand, TagCommandResult>
{
    private readonly ITagRepository _tagRepository;
    private readonly IClusterEventBus _eventBus;

    public CreateTagCommandHandler(
        ITagRepository tagRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus
    )
:base(idempotencyStore)    {
        _tagRepository = tagRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.TagCreate;

protected override async Task<CommandExecutionResult<TagCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<CreateTagCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (RejectIfBlank(command, command.Payload.Name, EntityRefs.TagNameRequired) is { } blankReject)
            return blankReject;

        if (TagCommandValidation.GetNameValidationError(command.Payload.Name) is { } validationError)
            return RejectInvariant(command, validationError);

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
        return await TagCommandFlow.ExecuteCreateAsync(
            command,
            _eventBus,
            ct => _tagRepository.CreateAsync(
                command.PrincipalId,
                command.Payload with { InsertedAtUtc = insertedAtUtc },
                ct),
            cancellationToken);
    }

}
