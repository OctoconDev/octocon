using Interfold.Settings.Contracts;
using Interfold.Settings.Contracts.Models.Commands;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Settings.Domain.Settings;

public sealed class UpdateDescriptionCommandHandler : IdempotentCommandHandler<UpdateDescriptionCommand, SettingsCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IClusterEventBus _eventBus;

    public UpdateDescriptionCommandHandler(
        IAccountRepository accountRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _accountRepository = accountRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsDescriptionUpdate;

protected override async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<UpdateDescriptionCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.Description.Length > 3000)
            return RejectInvariant(command, EntityRefs.SettingsDescriptionInvalid);

        return await SettingsIdempotentCommandFlow.ExecuteMutationAsync(
            command,
            ct => _accountRepository.UpdateDescriptionAsync(
                command.PrincipalId,
                command.Payload.Description,
                ct),
            EntityRefs.SettingsDescriptionUpdateFailed,
            SettingsAction.DescriptionUpdated,
            ct => _eventBus.PublishProfileUpdatedAsync(command.PrincipalId, includeUsername: false, ct),
            cancellationToken);
    }

}
