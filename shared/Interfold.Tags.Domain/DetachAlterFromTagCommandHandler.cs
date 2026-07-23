using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Tags;

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
