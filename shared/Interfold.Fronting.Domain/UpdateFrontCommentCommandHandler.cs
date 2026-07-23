using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Fronting;

public sealed class UpdateFrontCommentCommandHandler : IdempotentCommandHandler<UpdateFrontCommentCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IClusterEventBus _eventBus;

    public UpdateFrontCommentCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _frontingRepository = frontingRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FrontingUpdateComment;

protected override async Task<CommandExecutionResult<FrontCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<UpdateFrontCommentCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (FrontingCommandFlow.RejectIfInvalidComment(command, command.Payload.Comment) is { } invalidCommentReject)
            return invalidCommentReject;

        var (existing, rejection) = await FrontingCommandFlow.GetActiveFrontByIdAfterValidationOrRejectAsync(
            command,
            _frontingRepository,
            command.Payload.FrontId,
            cancellationToken);
        if (rejection is not null)
            return rejection;

        if (await FrontingCommandFlow.ExecuteMutationOrRejectAsync(
                command,
                ct => _frontingRepository.UpdateCommentByFrontIdAsync(
                    command.PrincipalId,
                    command.Payload.FrontId,
                    command.Payload.Comment ?? string.Empty,
                    ct),
                EntityRefs.FrontingUpdateCommentFailed,
                cancellationToken) is { } updateReject)
            return updateReject;

        // Emit granular event for socket layer to handle front_updated
        await _eventBus.PublishStateChangedAndCommentUpdatedAsync(command.PrincipalId, command.Payload.FrontId, cancellationToken);

        return FrontingCommandFlow.Success(command.PrincipalId, existing!.Front.AlterId, command.Payload.FrontId);
    }

}
