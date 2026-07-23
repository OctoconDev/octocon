using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Domain.Tags;

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
