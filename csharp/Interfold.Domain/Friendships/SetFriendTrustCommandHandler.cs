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

public sealed class SetFriendTrustCommandHandler : IdempotentCommandHandler<SetFriendTrustCommand, FriendshipCommandResult>
{
    private readonly IFriendshipRepository _repository;
    private readonly IClusterEventBus _eventBus;

    public SetFriendTrustCommandHandler(
        IFriendshipRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _repository = repository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FriendshipTrust;

protected override async Task<CommandExecutionResult<FriendshipCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<SetFriendTrustCommand> command,
        CancellationToken cancellationToken = default)
    {

        var canonicalFriendSystemId = FriendshipCommandNormalization.ComposePeerId(
            command.PrincipalId,
            command.Payload.FriendSystemId);

        if (await FriendshipCommandFlow.ExecuteMutationOrRejectAsync(
                command,
                ct => _repository.SetTrustedAsync(
                    command.PrincipalId,
                    canonicalFriendSystemId,
                    command.Payload.Trusted,
                    ct),
                EntityRefs.FriendshipNotFound,
                cancellationToken) is { } trustReject)
            return trustReject;

        if (command.Payload.Trusted)
        {
            await _eventBus.PublishAsync(
                new FriendshipTrustedEvent(command.PrincipalId, canonicalFriendSystemId),
                cancellationToken);
        }
        else
        {
            await _eventBus.PublishAsync(
                new FriendshipUntrustedEvent(command.PrincipalId, canonicalFriendSystemId),
                cancellationToken);
        }

        return FriendshipCommandFlow.Success(
            command.PrincipalId,
            canonicalFriendSystemId,
            command.Payload.Trusted ? FriendshipAction.Trusted : FriendshipAction.Untrusted);
    }

}
