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

public sealed class RelocateFieldCommandHandler : IdempotentCommandHandler<RelocateFieldCommand, SettingsCommandResult>
{
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IClusterEventBus _eventBus;

    public RelocateFieldCommandHandler(ISettingsFieldRepository fieldRepository, IIdempotencyStore idempotencyStore, IClusterEventBus eventBus)
        : base(idempotencyStore)
    {
        _fieldRepository = fieldRepository;
        _eventBus = eventBus;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsFieldRelocate;

    protected override Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<RelocateFieldCommand> command,
        CancellationToken cancellationToken)
        => SettingsIdempotentCommandFlow.ExecuteMutationAsync(
            command,
            ct => _fieldRepository.RelocateAsync(command.PrincipalId, command.Payload.FieldId, command.Payload.Index, ct),
            EntityRefs.SettingsActionFailed(SettingsAction.FieldRelocated),
            SettingsAction.FieldRelocated,
            ct => _eventBus.PublishAsync(new SettingsFieldsChangedEvent(command.PrincipalId), ct),
            cancellationToken);
}
