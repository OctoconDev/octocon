using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Domain.Fronting;

public sealed class DeleteFrontByIdCommandHandler : IdempotentCommandHandler<DeleteFrontByIdCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IClusterEventBus _eventBus;

    public DeleteFrontByIdCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _frontingRepository = frontingRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FrontingDelete;

protected override async Task<CommandExecutionResult<FrontCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<DeleteFrontByIdCommand> command,
        CancellationToken cancellationToken = default)
    {
        var (resolution, rejection) = await FrontingCommandFlow.ResolveFrontByIdAfterValidationOrRejectAsync(
            command,
            _frontingRepository,
            command.Payload.FrontId,
            cancellationToken);
        if (rejection is not null)
            return rejection;

        if (resolution!.WasActive)
        {
            if (await FrontingCommandFlow.ExecuteMutationOrRejectAsync(
                command,
                ct => _frontingRepository.EndByFrontIdAsync(command.PrincipalId, command.Payload.FrontId, ct),
                EntityRefs.FrontingDeleteFailed,
                cancellationToken) is { } deleteReject)
                return deleteReject;
        }

        if (await FrontingCommandFlow.ExecuteMutationOrRejectAsync(
            command,
            ct => _frontingRepository.DeleteFrontByIdAsync(command.PrincipalId, command.Payload.FrontId, ct),
            EntityRefs.FrontingDeleteFailed,
            cancellationToken) is { } historyDeleteReject)
            return historyDeleteReject;

        var alterId = resolution.AlterId;

        await _eventBus.PublishDeletedAsync(command.PrincipalId, command.Payload.FrontId, resolution.WasActive, cancellationToken);

        return FrontingCommandFlow.Success(command.PrincipalId, alterId, command.Payload.FrontId);
    }

}