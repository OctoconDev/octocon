using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Friendships;

public sealed class SendFriendRequestCommandHandler : IdempotentCommandHandler<SendFriendRequestCommand, FriendshipCommandResult>
{
    private readonly IFriendshipRepository _repository;
    private readonly IClusterEventBus _eventBus;

    public SendFriendRequestCommandHandler(
        IFriendshipRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _repository = repository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FriendRequestSend;

protected override async Task<CommandExecutionResult<FriendshipCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<SendFriendRequestCommand> command,
        CancellationToken cancellationToken = default)
    {

        var resolvedTargetSystemId = await _repository.ResolveUserIdAsync(
            command.Payload.TargetSystemId,
            cancellationToken);

        if (resolvedTargetSystemId is null)
        {
            return RejectInvariant(command, EntityRefs.FriendRequestNoUser);
        }

        var targetSystemId = resolvedTargetSystemId.Value;

        // The controller's route-segment self-check catches the trivial
        // "PUT /api/friend-requests/nam:principal-a" case without a repo hop; this
        // resolved-id check catches the routed-through-a-username / discord-id /
        // raw-bare-id shapes where the client couldn't (or didn't) know their own scoped
        // id. Both guards return the same rejection so callers see one uniform error.
        if (targetSystemId == command.PrincipalId.AsSystemId())
        {
            return RejectInvariant(command, EntityRefs.FriendRequestNoUser);
        }

        // The resolver returns a scoped-shape id today (the account repos all compose one
        // before returning). Route through Compose one more time so we still hand the
        // event publisher a ScopedSystemId if a repo path ever emits a bare id — the
        // principal's region is the safe fallback for the same-region friendship case.
        var targetScopedId = FriendshipCommandNormalization.ComposePeerId(
            command.PrincipalId,
            targetSystemId);

        var outcome = await _repository.SendRequestAsync(
            command.PrincipalId,
            targetSystemId,
            cancellationToken);

        if (outcome is SendFriendRequestOutcome.AlreadyFriends)
        {
            return RejectInvariant(command, EntityRefs.FriendRequestAlreadyFriends);
        }

        if (outcome is SendFriendRequestOutcome.AlreadySent)
        {
            return RejectInvariant(command, EntityRefs.FriendRequestAlreadySent);
        }

        if (outcome is SendFriendRequestOutcome.NoUser)
        {
            return RejectInvariant(command, EntityRefs.FriendRequestNoUser);
        }

        var action = outcome is SendFriendRequestOutcome.Accepted ? FriendshipAction.Accepted : FriendshipAction.Sent;

        //TODO: Check this path sends the required events
        if (outcome is SendFriendRequestOutcome.Accepted)
        {
            await _eventBus.PublishFriendshipAddedBothWaysAsync(
                command.PrincipalId,
                targetScopedId,
                cancellationToken);

            await _eventBus.PublishAsync(new FriendRequestRemovedToEvent(
                targetScopedId,
                command.PrincipalId), cancellationToken);
        }
        else
        {
            await _eventBus.PublishAsync(new FriendRequestSentEvent(
                command.PrincipalId,
                targetSystemId), cancellationToken);

            await _eventBus.PublishAsync(new FriendRequestReceivedEvent(
                targetScopedId,
                command.PrincipalId), cancellationToken);
        }

        return FriendshipCommandFlow.Success(command.PrincipalId, targetSystemId, action);
    }

}
