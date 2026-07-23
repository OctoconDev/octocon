using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Tags;

public sealed class RemoveParentTagCommandHandler : IdempotentCommandHandler<RemoveParentTagCommand, TagCommandResult>
{
    private readonly ITagRepository _tagRepository;
    private readonly IClusterEventBus _eventBus;

    public RemoveParentTagCommandHandler(
        ITagRepository tagRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _tagRepository = tagRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.TagRemoveParent;

protected override async Task<CommandExecutionResult<TagCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<RemoveParentTagCommand> command,
        CancellationToken cancellationToken = default)
    {
        var tagId = command.Payload.TagId;
        return await TagCommandFlow.ExecuteTagMutationAsync(
            command,
            tagId,
            ct => _tagRepository.RemoveParentAsync(command.PrincipalId, tagId, ct),
            EntityRefs.TagNotFound,
            _eventBus,
            cancellationToken);
    }

}
