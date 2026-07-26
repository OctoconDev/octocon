using Interfold.Friendships.Contracts;
using Interfold.Friendships.Contracts.Enums;
using Interfold.Friendships.Contracts.Models.Commands;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Friendships.Domain;

public sealed class AcceptFriendRequestCommandHandler : IdempotentCommandHandler<AcceptFriendRequestCommand, FriendshipCommandResult>
{
    private readonly IFriendshipRepository _repository;
    private readonly IClusterEventBus _eventBus;

    public AcceptFriendRequestCommandHandler(
        IFriendshipRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _repository = repository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FriendRequestAccept;

protected override async Task<CommandExecutionResult<FriendshipCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<AcceptFriendRequestCommand> command,
        CancellationToken cancellationToken = default)
    {

        var canonicalSourceSystemId = FriendshipCommandNormalization.ComposePeerId(
            command.PrincipalId,
            command.Payload.SourceSystemId);

        var canonicalPrincipalId = FriendshipCommandNormalization.CanonicalPrincipalForPeer(
            command.Payload.SourceSystemId,
            command.PrincipalId);

        var outcome = await _repository.AcceptRequestAsync(
            command.PrincipalId,
            canonicalSourceSystemId,
            cancellationToken);

        if (FriendshipCommandFlow.RejectIfMutationOutcomeFailed(command, outcome) is { } rejection)
            return rejection;

        await _eventBus.PublishFriendshipAddedBothWaysAsync(
            canonicalPrincipalId,
            canonicalSourceSystemId,
            cancellationToken);

        await _eventBus.PublishRequestRemovedFromThenToAsync(
            canonicalPrincipalId,
            canonicalSourceSystemId,
            cancellationToken);

        return FriendshipCommandFlow.Success(command.PrincipalId, canonicalSourceSystemId, FriendshipAction.Accepted);
    }

}
