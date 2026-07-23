using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Polls;

public sealed class DeletePollCommandHandler : IdempotentCommandHandler<DeletePollCommand, PollCommandResult>
{
    private readonly IPollRepository _pollRepository;
    private readonly IClusterEventBus _eventBus;

    public DeletePollCommandHandler(
        IPollRepository pollRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _pollRepository = pollRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.PollDelete;

protected override async Task<CommandExecutionResult<PollCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<DeletePollCommand> command,
        CancellationToken cancellationToken = default)
    {

        return await PollCommandFlow.ExecuteDeleteAsync(
            command,
            _pollRepository,
            _eventBus,
            cancellationToken);
    }

}
