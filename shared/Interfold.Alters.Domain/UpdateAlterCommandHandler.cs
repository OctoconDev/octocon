using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Alters;

public sealed class UpdateAlterCommandHandler : IdempotentCommandHandler<UpdateAlterCommand, AlterCommandResult>
{
    private readonly IAlterRepository _alterRepository;
    private readonly IClusterEventBus _eventBus;

    public UpdateAlterCommandHandler(
        IAlterRepository alterRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus
    )
:base(idempotencyStore)    {
        _alterRepository = alterRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.AlterUpdate;

protected override async Task<CommandExecutionResult<AlterCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<UpdateAlterCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (AlterCommandFlow.RejectIfInvalidAlterId(command, command.Payload.AlterId, EntityRefs.AlterId) is { } rangeReject)
            return rangeReject;

        if (!HasAnyMutableField(command.Payload))
        {
            return RejectInvariant(command, EntityRefs.AlterUpdateNoFields);
        }

        // avatar_url and avatar_source must move together. We accept both-set or both-null
        // (the ClearAvatar branch handles "set null"); a half-set request is rejected so we
        // never silently fall back to a guessed source on the repo side.
        if ((command.Payload.AvatarUrl is not null) != (command.Payload.AvatarSource is not null))
        {
            return RejectInvariant(command, EntityRefs.AlterAvatarSourceRequired);
        }

        if (await AlterCommandFlow.RejectIfAlterNotFoundAsync(command, _alterRepository, command.Payload.AlterId, EntityRefs.AlterNotFound, cancellationToken) is { } notFoundReject)
            return notFoundReject;

        if (!string.IsNullOrWhiteSpace(command.Payload.Alias))
        {
            var aliasTaken = await _alterRepository.AliasTakenByOtherAsync(
                command.PrincipalId,
                command.Payload.AlterId,
                command.Payload.Alias,
                cancellationToken
            );

            if (aliasTaken)
            {
                return RejectInvariant(command, EntityRefs.AlterAliasTaken);
            }
        }

        return await AlterCommandFlow.ExecuteMutationAsync(
            command,
            command.Payload.AlterId,
            ct => _alterRepository.UpdateAsync(command.PrincipalId, command.Payload, ct),
            EntityRefs.AlterUpdateFailed,
            ct => _eventBus.PublishAsync(new AlterUpdatedEvent(command.PrincipalId, command.Payload.AlterId), ct),
            cancellationToken);
    }

    private static bool HasAnyMutableField(UpdateAlterCommand payload) =>
        payload.Name is not null ||
        payload.Description is not null ||
        payload.AvatarUrl is not null ||
        payload.AvatarSource is not null ||
        payload.ClearAvatar ||
        payload.Color is not null ||
        payload.Pronouns is not null ||
        payload.SecurityLevel is not null ||
        payload.Fields is not null ||
        payload.ProxyName is not null ||
        payload.Alias is not null ||
        payload.Untracked is not null ||
        payload.Archived is not null ||
        payload.Pinned is not null;

}
