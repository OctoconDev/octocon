using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Auth;

public sealed record RevokeAuthTokenCommandResult(bool Success);

public sealed class RevokeAuthTokenCommandHandler : ICommandHandler<RevokeAuthTokenCommand, RevokeAuthTokenCommandResult>
{
    private readonly IAuthTokenRevocationRepository _tokenRevocation;

    public RevokeAuthTokenCommandHandler(IAuthTokenRevocationRepository tokenRevocation)
    {
        _tokenRevocation = tokenRevocation;
    }

    public async Task<CommandExecutionResult<RevokeAuthTokenCommandResult>> HandleAsync(CommandEnvelope<RevokeAuthTokenCommand> command, CancellationToken cancellationToken = default)
    {
        await _tokenRevocation.RevokeTokenAsync(command.Payload.Jti, cancellationToken);
        return CommandExecutionResult<RevokeAuthTokenCommandResult>.Success(new RevokeAuthTokenCommandResult(true));
    }
}
