using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;

namespace Interfold.Shared.Domain.Accounts;

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
