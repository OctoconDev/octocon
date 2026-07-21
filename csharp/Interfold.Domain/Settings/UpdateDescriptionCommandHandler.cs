using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Settings;

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
