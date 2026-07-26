using Interfold.Friendships.Contracts;
using Interfold.Friendships.Contracts.Enums;
using Interfold.Friendships.Contracts.Models.Commands;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Friendships.Domain;

public sealed class RejectFriendRequestCommandHandler : IdempotentCommandHandler<RejectFriendRequestCommand, FriendshipCommandResult>
{
    private readonly IFriendshipRepository _repository;
    private readonly IClusterEventBus _eventBus;

    public RejectFriendRequestCommandHandler(
        IFriendshipRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _repository = repository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FriendRequestReject;

protected override async Task<CommandExecutionResult<FriendshipCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<RejectFriendRequestCommand> command,
        CancellationToken cancellationToken = default)
    {

        var canonicalSourceSystemId = FriendshipCommandNormalization.ComposePeerId(
            command.PrincipalId,
            command.Payload.SourceSystemId);
        var canonicalPrincipalId = FriendshipCommandNormalization.CanonicalPrincipalForPeer(
            canonicalSourceSystemId,
            command.PrincipalId);

        var outcome = await _repository.RejectRequestAsync(
            command.PrincipalId,
            canonicalSourceSystemId,
            cancellationToken);

        if (FriendshipCommandFlow.RejectIfMutationOutcomeFailed(command, outcome) is { } rejection)
            return rejection;

        await _eventBus.PublishRequestRemovedFromThenToAsync(
            canonicalPrincipalId,
            canonicalSourceSystemId,
            cancellationToken);

        return FriendshipCommandFlow.Success(command.PrincipalId, canonicalSourceSystemId, FriendshipAction.Rejected);
    }

}
