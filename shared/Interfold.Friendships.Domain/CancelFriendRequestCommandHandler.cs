using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Domain.Friendships;

public sealed class CancelFriendRequestCommandHandler : IdempotentCommandHandler<CancelFriendRequestCommand, FriendshipCommandResult>
{
    private readonly IFriendshipRepository _repository;
    private readonly IClusterEventBus _eventBus;

    public CancelFriendRequestCommandHandler(
        IFriendshipRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _repository = repository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FriendRequestCancel;

protected override async Task<CommandExecutionResult<FriendshipCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<CancelFriendRequestCommand> command,
        CancellationToken cancellationToken = default)
    {
        
        var canonicalTargetSystemId = FriendshipCommandNormalization.ComposePeerId(
            command.PrincipalId,
            command.Payload.TargetSystemId);

        var canonicalPrincipalId = FriendshipCommandNormalization.CanonicalPrincipalForPeer(
            command.Payload.TargetSystemId,
            command.PrincipalId);

        var outcome = await _repository.CancelRequestAsync(
            command.PrincipalId,
            canonicalTargetSystemId,
            cancellationToken);

        if (FriendshipCommandFlow.RejectIfMutationOutcomeFailed(command, outcome) is { } rejection)
            return rejection;

        await _eventBus.PublishRequestRemovedToThenFromAsync(
            canonicalPrincipalId,
            canonicalTargetSystemId,
            cancellationToken);

        return FriendshipCommandFlow.Success(command.PrincipalId, canonicalTargetSystemId, FriendshipAction.Cancelled);
    }

}
