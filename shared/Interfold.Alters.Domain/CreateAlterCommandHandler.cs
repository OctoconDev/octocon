using Interfold.Alters.Contracts;
using Interfold.Alters.Contracts.Events;
using Interfold.Alters.Contracts.Models.Commands;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Alters.Domain;

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
