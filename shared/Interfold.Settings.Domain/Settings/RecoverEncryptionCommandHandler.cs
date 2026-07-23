using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;
using Interfold.Contracts.Configuration;
using Interfold.Contracts;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Settings;

public sealed class RecoverEncryptionCommandHandler : IdempotentCommandHandler<RecoverEncryptionCommand, EncryptionCommandResult>
{
    private readonly IEncryptionStateRepository _repository;
    private readonly IOptionsMonitor<AuthenticationConfiguration> _authOptions;

    public RecoverEncryptionCommandHandler(
        IEncryptionStateRepository repository,
        IIdempotencyStore idempotencyStore,
        IOptionsMonitor<AuthenticationConfiguration> authOptions)
:base(idempotencyStore)    {
        _repository = repository;
        _authOptions = authOptions;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsEncryptionRecover;

protected override async Task<CommandExecutionResult<EncryptionCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<RecoverEncryptionCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (RejectIfBlank(command, command.Payload.RecoveryCode.Value, EntityRefs.SettingsRecoveryCodeInvalid) is { } blankReject)
            return blankReject;

        var state = await _repository.GetAsync(command.PrincipalId, cancellationToken);
        if (state is not { Initialized: true, KeyChecksum: { } keyChecksum, Salt: { } salt }
            || string.IsNullOrWhiteSpace(keyChecksum.Value) || string.IsNullOrWhiteSpace(salt.Value))
            return RejectInvariant(command, EntityRefs.SettingsEncryptionNotInitialized);

        var pepper = _authOptions.CurrentValue.EncryptionPepper;
        // `checksum == keyChecksum` uses the record-struct value equality (ordinal string
        // equality on the underlying .Value), matching `string.Equals(..., Ordinal)` exactly.
        var key = EncryptionKey.DeriveKey(pepper, command.PrincipalId, command.Payload.RecoveryCode, salt);
        var checksum = EncryptionKey.DeriveChecksum(key);

        if (checksum != keyChecksum)
            return RejectInvariant(command, EntityRefs.SettingsInvalidRecoveryCode);

        var result = new EncryptionCommandResult(command.PrincipalId, EncryptionAction.EncryptionRecovered, key, Replay: false);

        return CommandExecutionResult<EncryptionCommandResult>.Success(result);
    }

}
