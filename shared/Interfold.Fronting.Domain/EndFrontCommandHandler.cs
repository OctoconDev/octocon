using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Fronting;

public sealed class EndFrontCommandHandler : IdempotentCommandHandler<EndFrontCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IClusterEventBus _eventBus;
    private readonly TimeProvider _timeProvider;

    public EndFrontCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus,
        TimeProvider timeProvider) : base(idempotencyStore)
    {
        _frontingRepository = frontingRepository;
        _eventBus = eventBus;
        _timeProvider = timeProvider;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FrontingEnd;


    protected override async Task<CommandExecutionResult<FrontCommandResult>> ExecuteCoreAsync(
            CommandEnvelope<EndFrontCommand> command,
            CancellationToken cancellationToken = default
        )
    {
        if (await FrontingCommandFlow.RejectIfInvalidOrNotFrontingAsync(command, _frontingRepository, command.Payload.AlterId, cancellationToken) is { } notFrontingReject)
            return notFrontingReject;

        var activeFronts = await _frontingRepository.ListActiveAsync(command.PrincipalId, cancellationToken);
        var endedFrontWasPrimary = activeFronts.Any(front =>
            front.Alter.Id == command.Payload.AlterId && front.Primary);

        if (await FrontingCommandFlow.ExecuteMutationOrRejectAsync(
            command,
            ct => _frontingRepository.EndAsync(command.PrincipalId, command.Payload.AlterId, _timeProvider.GetUtcNow(), ct),
            EntityRefs.FrontingEndFailed,
            cancellationToken) is { } endReject)
            return endReject;

        // Emit granular event for socket layer to handle fronting_ended
        await _eventBus.PublishStateChangedAndEndedAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);

        await _eventBus.PublishPrimaryClearedIfNeededAsync(
            command.PrincipalId,
            endedFrontWasPrimary,
            cancellationToken);

        return FrontingCommandFlow.Success(command.PrincipalId, command.Payload.AlterId, frontId: null);
    }

}