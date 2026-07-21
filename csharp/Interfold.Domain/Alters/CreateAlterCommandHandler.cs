using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Alters;

public sealed class CreateAlterCommandHandler : IdempotentCommandHandler<CreateAlterCommand, AlterCommandResult>
{
    private readonly IAlterRepository _alterRepository;
    private readonly IClusterEventBus _eventBus;

    public CreateAlterCommandHandler(
        IAlterRepository alterRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus
    )
:base(idempotencyStore)    {
        _alterRepository = alterRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.AlterCreate;

protected override async Task<CommandExecutionResult<AlterCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<CreateAlterCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (RejectIfBlank(command, command.Payload.Name, EntityRefs.AlterName) is { } blankReject)
            return blankReject;

        return await AlterCommandFlow.ExecuteCreateAsync(
            command,
            ct => _alterRepository.CreateAsync(command.PrincipalId, command.Payload, ct),
            EntityRefs.AlterCreate,
            (alterId, ct) => _eventBus.PublishAsync(new AlterCreatedEvent(command.PrincipalId, alterId), ct),
            cancellationToken);
    }

}