using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;

namespace Interfold.Shared.Domain.Auth;

public sealed record RecordAuthTokenCommandResult(bool Success);

public sealed class RecordAuthTokenCommandHandler : ICommandHandler<RecordAuthTokenCommand, RecordAuthTokenCommandResult>
{
    private readonly IAuthTokenRevocationRepository _tokenRevocation;

    public RecordAuthTokenCommandHandler(IAuthTokenRevocationRepository tokenRevocation)
    {
        _tokenRevocation = tokenRevocation;
    }

    public async Task<CommandExecutionResult<RecordAuthTokenCommandResult>> HandleAsync(CommandEnvelope<RecordAuthTokenCommand> command, CancellationToken cancellationToken = default)
    {
        await _tokenRevocation.RecordTokenAsync(command.Payload.Jti, command.Payload.SystemId, command.Payload.ExpiresAt, cancellationToken);
        return CommandExecutionResult<RecordAuthTokenCommandResult>.Success(new RecordAuthTokenCommandResult(true));
    }
}
