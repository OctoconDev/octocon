using Interfold.Fronting.Contracts;
using Interfold.Fronting.Contracts.Models.Commands;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Settings.Contracts.Events;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Fronting.Domain;

public sealed class SetPrimaryFrontCommandHandler : IdempotentCommandHandler<SetPrimaryFrontCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IClusterEventBus _eventBus;

    public SetPrimaryFrontCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus
    )
:base(idempotencyStore)    {
        _frontingRepository = frontingRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FrontingPrimary;

protected override async Task<CommandExecutionResult<FrontCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<SetPrimaryFrontCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (command.Payload.AlterId is { } alterId)
        {
            if (await FrontingCommandFlow.RejectIfInvalidOrNotFrontingAsync(command, _frontingRepository, alterId, cancellationToken) is { } notFrontingReject)
                return notFrontingReject;
        }

        if (await FrontingCommandFlow.ExecuteMutationOrRejectAsync(
            command,
            ct => _frontingRepository.SetPrimaryAsync(command.PrincipalId, command.Payload.AlterId, ct),
            EntityRefs.FrontingPrimaryFailed,
            cancellationToken) is { } setReject)
            return setReject;

        await _eventBus.PublishStateChangedAndPrimaryChangedAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        // Inlined: SettingsEventBusExtensions.PublishProfileUpdatedAsync is internal to
        // Interfold.Shared.Domain and this handler lives in Interfold.Fronting.Domain after the
        // Phase-3 slice-3 move. The event itself (Interfold.Shared.Contracts.Events) is public.
        await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, EmitUsernameUpdated: false), cancellationToken);

        return FrontingCommandFlow.Success(command.PrincipalId, command.Payload.AlterId, frontId: null);
    }

}