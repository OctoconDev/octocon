using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Accounts;

public sealed record CreateLinkTokenCommandResult(LinkToken Token);

public sealed class CreateLinkTokenCommandHandler : ICommandHandler<CreateLinkTokenCommand, CreateLinkTokenCommandResult>
{
    private readonly IAccountRepository _accountRepository;

    public CreateLinkTokenCommandHandler(IAccountRepository accountRepository)
    {
        _accountRepository = accountRepository;
    }

    public async Task<CommandExecutionResult<CreateLinkTokenCommandResult>> HandleAsync(CommandEnvelope<CreateLinkTokenCommand> command, CancellationToken cancellationToken = default)
    {
        var token = await _accountRepository.GetOrCreateLinkTokenAsync(command.PrincipalId, cancellationToken);
        return CommandExecutionResult<CreateLinkTokenCommandResult>.Success(new CreateLinkTokenCommandResult(token));
    }
}
