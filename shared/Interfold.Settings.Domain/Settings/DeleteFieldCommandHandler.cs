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

public sealed class DeleteFieldCommandHandler : IdempotentCommandHandler<DeleteFieldCommand, SettingsCommandResult>
{
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IClusterEventBus _eventBus;

    public DeleteFieldCommandHandler(ISettingsFieldRepository fieldRepository, IIdempotencyStore idempotencyStore, IClusterEventBus eventBus)
        : base(idempotencyStore)
    {
        _fieldRepository = fieldRepository;
        _eventBus = eventBus;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsFieldDelete;

    protected override Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<DeleteFieldCommand> command,
        CancellationToken cancellationToken)
        => SettingsIdempotentCommandFlow.ExecuteMutationAsync(
            command,
            ct => _fieldRepository.DeleteAsync(command.PrincipalId, command.Payload.FieldId, ct),
            EntityRefs.SettingsActionFailed(SettingsAction.FieldDeleted),
            SettingsAction.FieldDeleted,
            ct => _eventBus.PublishAsync(new SettingsFieldsChangedEvent(command.PrincipalId), ct),
            cancellationToken);
}
