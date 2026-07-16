using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Accounts;

public sealed class UpdateUsernameCommandHandler : ICommandHandler<UpdateUsernameCommand, AccountCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public UpdateUsernameCommandHandler(
        IAccountRepository accountRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus
    )
    {
        _accountRepository = accountRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<AccountCommandResult>> HandleAsync(
        CommandEnvelope<UpdateUsernameCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Username))
        {
            return RejectInvariant(command, EntityRefs.AccountUsernameInvalid);
        }

        if (command.Payload.Username.Value.Length > 64)
        {
            return RejectInvariant(command, EntityRefs.AccountUsernameTooLong);
        }

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            cancellationToken
        );

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return RejectDuplicate(command, EntityRefs.AccountUsernameUpdate);
            }

            var replay = CommandSerialization.Deserialize<AccountCommandResult>(previous.OutcomePayload);
            if (replay is not null)
            {
                return CommandExecutionResult<AccountCommandResult>.Success(replay with { Replay = true });
            }
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
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken
        );

        await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, true), cancellationToken);
        return CommandExecutionResult<AccountCommandResult>.Success(result);
    }

    private static CommandExecutionResult<AccountCommandResult> RejectDuplicate(
        CommandEnvelope<UpdateUsernameCommand> command,
        EntityRef entityRef
    ) =>
        CommandExecutionResult<AccountCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictDuplicate,
                command.OperationId,
                entityRef,
                ResolutionHint.NoRetry
            )
        );

    private static CommandExecutionResult<AccountCommandResult> RejectInvariant(
        CommandEnvelope<UpdateUsernameCommand> command,
        EntityRef entityRef
    ) =>
        CommandExecutionResult<AccountCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictInvariant,
                command.OperationId,
                entityRef,
                ResolutionHint.ManualMergeRequired
            )
        );
}
