using Interfold.Friendships.Contracts;
using Interfold.Friendships.Contracts.Enums;
using Interfold.Friendships.Contracts.Models.Commands;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Friendships.Domain;

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
