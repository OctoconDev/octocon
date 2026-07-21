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
