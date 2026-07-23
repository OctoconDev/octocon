using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Domain.Settings;

public sealed class UpdateFieldCommandHandler : IdempotentCommandHandler<UpdateFieldCommand, SettingsCommandResult>
{
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IClusterEventBus _eventBus;

    public UpdateFieldCommandHandler(ISettingsFieldRepository fieldRepository, IIdempotencyStore idempotencyStore, IClusterEventBus eventBus)
        : base(idempotencyStore)
    {
        _fieldRepository = fieldRepository;
        _eventBus = eventBus;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsFieldUpdate;

    protected override Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<UpdateFieldCommand> command,
        CancellationToken cancellationToken)
        => SettingsIdempotentCommandFlow.ExecuteMutationAsync(
            command,
            ct => _fieldRepository.UpdateAsync(command.PrincipalId, command.Payload.FieldId, command.Payload.Name, command.Payload.SecurityLevel, command.Payload.Locked, ct),
            EntityRefs.SettingsActionFailed(SettingsAction.FieldUpdated),
            SettingsAction.FieldUpdated,
            ct => _eventBus.PublishAsync(new SettingsFieldsChangedEvent(command.PrincipalId), ct),
            cancellationToken);
}
