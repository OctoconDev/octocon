using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Settings;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Domain.Accounts;

public sealed class UpdateUsernameCommandHandler : IdempotentCommandHandler<UpdateUsernameCommand, AccountCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IClusterEventBus _eventBus;

    public UpdateUsernameCommandHandler(
        IAccountRepository accountRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus
    )
:base(idempotencyStore)    {
        _accountRepository = accountRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.AccountUsernameUpdate;

protected override async Task<CommandExecutionResult<AccountCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<UpdateUsernameCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (RejectIfBlank(command, command.Payload.Username, EntityRefs.AccountUsernameInvalid) is { } blankReject)
            return blankReject;

        if (command.Payload.Username.Value.Length > 64)
        {
            return RejectInvariant(command, EntityRefs.AccountUsernameTooLong);
        }

        var persisted = await _accountRepository.UpdateUsernameAsync(
            command.PrincipalId,
            command.Payload.Username,
            cancellationToken
        );

        if (!persisted)
        {
            return RejectInvariant(command, EntityRefs.AccountUsernameUpdateFailed);
        }

        var result = new AccountCommandResult(command.PrincipalId, command.Payload.Username, Replay: false);

        await _eventBus.PublishProfileUpdatedAsync(command.PrincipalId, includeUsername: true, cancellationToken);
        return CommandExecutionResult<AccountCommandResult>.Success(result);
    }

}
