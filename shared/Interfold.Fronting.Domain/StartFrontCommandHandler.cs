using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Domain.Fronting;

public sealed class StartFrontCommandHandler : IdempotentCommandHandler<StartFrontCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IClusterEventBus _eventBus;
    private readonly TimeProvider _timeProvider;

    public StartFrontCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus,
        TimeProvider timeProvider) : base(idempotencyStore)
    {
        _frontingRepository = frontingRepository;
        _eventBus = eventBus;
        _timeProvider = timeProvider;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FrontingStart;


    protected override async Task<CommandExecutionResult<FrontCommandResult>> ExecuteCoreAsync(
            CommandEnvelope<StartFrontCommand> command,
            CancellationToken cancellationToken = default
        )
    {
        if (FrontingCommandFlow.RejectIfInvalidAlterId(command, command.Payload.AlterId) is { } rangeReject)
            return rangeReject;

        if (FrontingCommandFlow.RejectIfInvalidComment(command, command.Payload.Comment) is { } invalidCommentReject)
            return invalidCommentReject;

        if (await FrontingCommandFlow.RejectIfAlreadyFrontingAsync(command, _frontingRepository, command.Payload.AlterId, cancellationToken) is { } alreadyFrontingReject)
            return alreadyFrontingReject;

        var frontId = await _frontingRepository.StartAsync(
            command.PrincipalId,
            command.Payload.AlterId,
            command.Payload.Comment,
            _timeProvider.GetUtcNow(),
            cancellationToken
        );

        var (startedFrontId, startedFrontRejection) = FrontingCommandFlow.GetStartedFrontIdOrReject(command, frontId);
        if (startedFrontRejection is not null)
            return startedFrontRejection;

        // Emit granular event for socket layer to handle fronting_started
        await _eventBus.PublishStateChangedAndStartedAsync(command.PrincipalId, startedFrontId!.Value, cancellationToken);

        return FrontingCommandFlow.Success(command.PrincipalId, command.Payload.AlterId, startedFrontId);
    }

}