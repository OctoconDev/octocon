using Interfold.Auth.Contracts.Configuration;
using Interfold.Settings.Contracts;
using Interfold.Settings.Contracts.Models.Commands;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Microsoft.Extensions.Options;

namespace Interfold.Settings.Domain.Settings;

public sealed class SetupEncryptionCommandHandler : IdempotentCommandHandler<SetupEncryptionCommand, EncryptionCommandResult>
{
    private readonly IEncryptionStateRepository _repository;
    private readonly IClusterEventBus _eventBus;
    private readonly IOptionsMonitor<AuthenticationConfiguration> _authOptions;

    public SetupEncryptionCommandHandler(
        IEncryptionStateRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus,
        IOptionsMonitor<AuthenticationConfiguration> authOptions)
:base(idempotencyStore)    {
        _repository = repository;
        _eventBus = eventBus;
        _authOptions = authOptions;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsEncryptionSetup;

protected override async Task<CommandExecutionResult<EncryptionCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<SetupEncryptionCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (RejectIfBlank(command, command.Payload.RecoveryCode.Value, EntityRefs.SettingsRecoveryCodeInvalid) is { } blankReject)
            return blankReject;
        
        var existing = await _repository.GetAsync(command.PrincipalId, cancellationToken);
        if (existing?.Salt is not { } salt || string.IsNullOrWhiteSpace(salt.Value))
        {
            throw new InterfoldException("Salt must be provided.", ErrorCodes.EncryptionSaltRequired);
        }

        var pepper = _authOptions.CurrentValue.EncryptionPepper;
        var key = EncryptionKey.DeriveKey(pepper, command.PrincipalId, command.Payload.RecoveryCode, salt);
        var checksum = EncryptionKey.DeriveChecksum(key);

        var persisted = await _repository.UpsertAsync(command.PrincipalId, true, checksum, null, cancellationToken);
        if (!persisted)
            return RejectInvariant(command, EntityRefs.SettingsEncryptionSetupFailed);

        var result = new EncryptionCommandResult(command.PrincipalId, EncryptionAction.EncryptionSetup, key, Replay: false);

        await _eventBus.PublishProfileUpdatedAsync(command.PrincipalId, includeUsername: false, cancellationToken);
        return CommandExecutionResult<EncryptionCommandResult>.Success(result);
    }

}
