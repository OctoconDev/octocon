using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Friendships;

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
