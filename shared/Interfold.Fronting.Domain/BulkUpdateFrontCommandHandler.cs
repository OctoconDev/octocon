using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Fronting;

public sealed class BulkUpdateFrontCommandHandler : IdempotentCommandHandler<BulkUpdateFrontCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IClusterEventBus _eventBus;
    private readonly TimeProvider _timeProvider;

    public BulkUpdateFrontCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus,
        TimeProvider timeProvider) : base(idempotencyStore)
    {
        _frontingRepository = frontingRepository;
        _eventBus = eventBus;
        _timeProvider = timeProvider;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FrontingBulkUpdate;


    protected override async Task<CommandExecutionResult<FrontCommandResult>> ExecuteCoreAsync(
            CommandEnvelope<BulkUpdateFrontCommand> command,
            CancellationToken cancellationToken = default)
    {
        if (FrontingCommandFlow.RejectIfAnyInvalidAlterId(
                command,
                command.Payload.Start.Select(x => x.AlterId).Concat(command.Payload.End)) is { } invalidAlterIdReject)
            return invalidAlterIdReject;

        var invalidStartComment = command.Payload.Start.FirstOrDefault(x => !FrontId.IsValidComment(x.Comment));
        if (invalidStartComment is not null &&
            FrontingCommandFlow.RejectIfInvalidComment(command, invalidStartComment.Comment) is { } invalidCommentReject)
            return invalidCommentReject;

        var operationTime = _timeProvider.GetUtcNow();
        await FrontingCommandFlow.EndAltersBestEffortAsync(
            _frontingRepository,
            command.PrincipalId,
            command.Payload.End,
            operationTime,
            cancellationToken);

        await FrontingCommandFlow.StartAltersIfNotFrontingAsync(
            _frontingRepository,
            command.PrincipalId,
            command.Payload.Start,
            operationTime,
            cancellationToken);

        // Emit granular event for socket layer to handle fronting_bulk
        await _eventBus.PublishStateChangedAndBulkUpdatedAsync(command.PrincipalId, cancellationToken);

        return FrontingCommandFlow.Success(command.PrincipalId, alterId: null, frontId: null);
    }

}