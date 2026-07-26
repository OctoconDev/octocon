using Interfold.Settings.Contracts;
using Interfold.Settings.Contracts.Events;
using Interfold.Settings.Contracts.Models.Commands;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Settings.Domain.Settings;

public sealed class ResetEncryptionCommandHandler : IdempotentCommandHandler<ResetEncryptionCommand, SettingsCommandResult>
{
    private readonly IEncryptionStateRepository _repository;
    private readonly IClusterEventBus _eventBus;

    public ResetEncryptionCommandHandler(
        IEncryptionStateRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _repository = repository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsEncryptionReset;

protected override async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<ResetEncryptionCommand> command,
        CancellationToken cancellationToken = default)
    {
        
        var persisted = await _repository.UpsertAsync(command.PrincipalId, false, null, null, cancellationToken);
        if (!persisted)
            return RejectInvariant(command, EntityRefs.SettingsEncryptionResetFailed);

        var result = new SettingsCommandResult(command.PrincipalId, SettingsAction.EncryptionReset, Replay: false);

        await _eventBus.PublishAsync(new SettingsEncryptedDataWipedSignalEvent(command.PrincipalId), cancellationToken);
        return CommandExecutionResult<SettingsCommandResult>.Success(result);
    }

}
