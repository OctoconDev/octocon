using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Friendships;

public sealed class RemoveFriendshipCommandHandler : IdempotentCommandHandler<RemoveFriendshipCommand, FriendshipCommandResult>
{
    private readonly IFriendshipRepository _repository;
    private readonly IClusterEventBus _eventBus;

    public RemoveFriendshipCommandHandler(
        IFriendshipRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _repository = repository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FriendshipRemove;

protected override async Task<CommandExecutionResult<FriendshipCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<RemoveFriendshipCommand> command,
        CancellationToken cancellationToken = default)
    {

        var canonicalFriendSystemId = FriendshipCommandNormalization.ComposePeerId(
            command.PrincipalId,
            command.Payload.FriendSystemId);
        var canonicalPrincipalId = FriendshipCommandNormalization.CanonicalPrincipalForPeer(
            canonicalFriendSystemId,
            command.PrincipalId);

        if (await FriendshipCommandFlow.ExecuteMutationOrRejectAsync(
                command,
                ct => _repository.RemoveFriendshipAsync(command.PrincipalId, canonicalFriendSystemId, ct),
                EntityRefs.FriendshipNotFound,
                cancellationToken) is { } removalReject)
            return removalReject;

        await _eventBus.PublishFriendshipRemovedBothWaysAsync(
            canonicalPrincipalId,
            canonicalFriendSystemId,
            cancellationToken);

        return FriendshipCommandFlow.Success(command.PrincipalId, canonicalFriendSystemId, FriendshipAction.Removed);
    }

}
