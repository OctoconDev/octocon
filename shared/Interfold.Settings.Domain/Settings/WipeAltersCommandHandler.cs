using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Settings;

public sealed class WipeAltersCommandHandler : IdempotentCommandHandler<WipeAltersCommand, SettingsCommandResult>
{
    private readonly IClusterEventBus _eventBus;
    private readonly IAlterRepository _alterRepository;

    public WipeAltersCommandHandler(
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus,
        IAlterRepository alterRepository)
        : base(idempotencyStore)
    {
        _eventBus = eventBus;
        _alterRepository = alterRepository;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsAltersWipe;

    protected override Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<WipeAltersCommand> command,
        CancellationToken cancellationToken)
        => SettingsIdempotentCommandFlow.ExecuteMutationAsync(
            command,
            async ct =>
            {
                var systemId = command.PrincipalId;
                var alters = await _alterRepository.ListAsync(systemId, ct);
                foreach (var alter in alters)
                {
                    await _alterRepository.DeleteAsync(systemId, alter.Id, ct);
                    //TODO: Delete alter image if it exists
                }

                return true;
            },
            EntityRefs.SettingsActionFailed(SettingsAction.AltersWiped),
            SettingsAction.AltersWiped,
            ct => _eventBus.PublishAsync(new SettingsAltersWipedSignalEvent(command.PrincipalId), ct),
            cancellationToken);
}
