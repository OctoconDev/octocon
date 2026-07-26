using Interfold.Settings.Contracts.Models.Commands;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Settings.Domain.Accounts;

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
