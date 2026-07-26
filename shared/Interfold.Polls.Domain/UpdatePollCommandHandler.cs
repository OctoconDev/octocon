using Interfold.Polls.Contracts;
using Interfold.Polls.Contracts.Models.Commands;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Polls.Domain;

public sealed class UpdatePollCommandHandler : IdempotentCommandHandler<UpdatePollCommand, PollCommandResult>
{
    private readonly IPollRepository _pollRepository;
    private readonly IClusterEventBus _eventBus;

    public UpdatePollCommandHandler(
        IPollRepository pollRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _pollRepository = pollRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.PollUpdate;

protected override async Task<CommandExecutionResult<PollCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<UpdatePollCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (PollCommandValidation.HasNoMutableFields(command.Payload))
            return RejectInvariant(command, EntityRefs.PollNoFields);

        if (PollCommandValidation.GetTitleDescriptionValidationError(command.Payload.Title, command.Payload.Description) is { } validationError)
            return RejectInvariant(command, validationError);

        return await PollCommandFlow.ExecuteUpdateAsync(
            command,
            _pollRepository,
            _eventBus,
            cancellationToken);
    }

}
