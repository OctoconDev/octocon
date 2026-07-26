using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Tags.Contracts;
using Interfold.Tags.Contracts.Events;
using Interfold.Tags.Contracts.Models.Commands;
using Interfold.Tags.Domain.Abstractions.Repository;

namespace Interfold.Tags.Domain;

public sealed class DeleteTagCommandHandler : IdempotentCommandHandler<DeleteTagCommand, TagCommandResult>
{
    private readonly ITagRepository _tagRepository;
    private readonly IClusterEventBus _eventBus;

    public DeleteTagCommandHandler(
        ITagRepository tagRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _tagRepository = tagRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.TagDelete;

protected override async Task<CommandExecutionResult<TagCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<DeleteTagCommand> command,
        CancellationToken cancellationToken = default)
    {
        var tagId = command.Payload.TagId;

        return await TagCommandFlow.ExecuteTagMutationAsync(
            command,
            tagId,
            ct => _tagRepository.DeleteAsync(command.PrincipalId, tagId, ct),
            EntityRefs.TagNotFound,
            ct => _eventBus.PublishAsync(new TagDeletedEvent(command.PrincipalId, tagId), ct),
            cancellationToken);
    }

}
