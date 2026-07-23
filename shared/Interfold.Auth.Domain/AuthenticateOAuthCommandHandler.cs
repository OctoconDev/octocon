using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Operations;

namespace Interfold.Domain.Auth;

public sealed class AuthenticateOAuthCommandHandler : ICommandHandler<AuthenticateOAuthCommand, SystemId>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IEncryptionStateRepository _encryptionRepository;

    public AuthenticateOAuthCommandHandler(
        IAccountRepository accountRepository,
        IEncryptionStateRepository encryptionRepository)
    {
        _accountRepository = accountRepository;
        _encryptionRepository = encryptionRepository;
    }

    public async Task<CommandExecutionResult<SystemId>> HandleAsync(CommandEnvelope<AuthenticateOAuthCommand> command, CancellationToken cancellationToken = default)
    {
        var identity = command.Payload.Identity;
        
        var resolvedSystemId = await _accountRepository.FindOrCreateSystemIdAsync(identity, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolvedSystemId?.Value))
            return CommandHandler.RejectInvariant<SystemId>(command.OperationId, EntityRefs.AuthLoginFailed);

        var systemId = resolvedSystemId.Value;
        var encryptionState = await _encryptionRepository.GetAsync(systemId, cancellationToken);
        
        if (encryptionState?.Salt == null)
        {
            await _encryptionRepository.UpsertAsync(systemId, false, null, EncryptionSalt.NewRandom(), cancellationToken);
        }

        return CommandExecutionResult<SystemId>.Success(systemId);
    }
}
