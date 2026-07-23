using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Tags;

public sealed class UpdateTagCommandHandler : IdempotentCommandHandler<UpdateTagCommand, TagCommandResult>
{
    private readonly ITagRepository _tagRepository;
    private readonly IClusterEventBus _eventBus;

    public UpdateTagCommandHandler(
        ITagRepository tagRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _tagRepository = tagRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.TagUpdate;

protected override async Task<CommandExecutionResult<TagCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<UpdateTagCommand> command,
        CancellationToken cancellationToken = default)
    {
        var payload = command.Payload;

        if (TagCommandValidation.HasNoMutableFields(payload))
            return RejectInvariant(command, EntityRefs.TagNoFields);

        if (TagCommandValidation.GetNameValidationError(payload.Name) is { } validationError)
            return RejectInvariant(command, validationError);

        return await TagCommandFlow.ExecuteTagMutationAsync(
            command,
            payload.TagId,
            ct => _tagRepository.UpdateAsync(command.PrincipalId, payload, ct),
            EntityRefs.TagNotFound,
            _eventBus,
            cancellationToken);
    }

}
