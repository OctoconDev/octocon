using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;
using Interfold.Contracts.Configuration;
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
