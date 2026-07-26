using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Tags.Contracts;
using Interfold.Tags.Contracts.Models.Commands;
using Interfold.Tags.Domain.Abstractions.Repository;

namespace Interfold.Tags.Domain;

public sealed class DetachAlterFromTagCommandHandler : IdempotentCommandHandler<DetachAlterFromTagCommand, TagCommandResult>
{
    private readonly ITagRepository _tagRepository;
    private readonly IAlterExistenceCheck _alterExistence;
    private readonly IClusterEventBus _eventBus;

    public DetachAlterFromTagCommandHandler(
        ITagRepository tagRepository,
        IAlterExistenceCheck alterExistence,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _tagRepository = tagRepository;
        _alterExistence = alterExistence;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.TagDetachAlter;

protected override async Task<CommandExecutionResult<TagCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<DetachAlterFromTagCommand> command,
        CancellationToken cancellationToken = default)
    {
        var payload = command.Payload;

        if (await TagCommandFlow.RejectIfAlterNotFoundAsync(command, payload.AlterId, _alterExistence, cancellationToken) is { } rejection)
            return rejection;

        return await TagCommandFlow.ExecuteTagMutationAsync(
            command,
            payload.TagId,
            ct => _tagRepository.DetachAlterAsync(command.PrincipalId, payload.TagId, payload.AlterId, ct),
            EntityRefs.TagNotFound,
            _eventBus,
            cancellationToken);
    }

}
