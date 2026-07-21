using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Tags;

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
