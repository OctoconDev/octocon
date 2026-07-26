using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Tags.Contracts;
using Interfold.Tags.Contracts.Ids;
using Interfold.Tags.Contracts.Models.Commands;
using Interfold.Tags.Domain.Abstractions.Repository;

namespace Interfold.Tags.Domain;

public sealed class SetParentTagCommandHandler : IdempotentCommandHandler<SetParentTagCommand, TagCommandResult>
{
    private readonly ITagRepository _tagRepository;
    private readonly IClusterEventBus _eventBus;

    public SetParentTagCommandHandler(
        ITagRepository tagRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _tagRepository = tagRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.TagSetParent;

protected override async Task<CommandExecutionResult<TagCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<SetParentTagCommand> command,
        CancellationToken cancellationToken = default)
    {
        var payload = command.Payload;

        if (payload.TagId == payload.ParentTagId)
            return RejectInvariant(command, EntityRefs.TagCycle);

        // Cycle detection: walk up from the proposed parent — if we encounter TagId, it's a cycle.
        if (await WouldCreateCycleAsync(command.PrincipalId, payload.ParentTagId, payload.TagId, cancellationToken))
            return RejectInvariant(command, EntityRefs.TagCycle);

        // SetParentAsync returns false if tag or parent tag does not exist.
        return await TagCommandFlow.ExecuteTagMutationAsync(
            command,
            payload.TagId,
            ct => _tagRepository.SetParentAsync(command.PrincipalId, payload.TagId, payload.ParentTagId, ct),
            EntityRefs.TagNotFound,
            _eventBus,
            cancellationToken);
    }

    /// <summary>
    /// Walks up the ancestor chain starting at <paramref name="candidateParentId"/>.
    /// Returns true if <paramref name="childId"/> appears, indicating a cycle.
    /// </summary>
    private async Task<bool> WouldCreateCycleAsync(
        SystemId systemId, TagId candidateParentId, TagId childId, CancellationToken cancellationToken)
    {
        TagId? current = candidateParentId;
        while (current is not null)
        {
            if (current == childId) return true;
            current = await _tagRepository.GetParentIdAsync(systemId, current.Value, cancellationToken);
        }
        return false;
    }

}
