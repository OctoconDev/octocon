using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Friendships;

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
