using Interfold.Contracts;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Alters;

internal static class AlterCommandFlow
{
    public static CommandExecutionResult<AlterCommandResult> Success(
        ScopedSystemId systemId,
        AlterId alterId)
        => CommandExecutionResult<AlterCommandResult>.Success(new AlterCommandResult(systemId, alterId, Replay: false));

    public static CommandExecutionResult<AlterCommandResult>? RejectIfInvalidAlterId<TCommand>(
        CommandEnvelope<TCommand> command,
        AlterId alterId,
        EntityRef invalidAlterIdEntityRef)
        => CommandHandler.RejectIfAlterIdOutOfRange<AlterCommandResult>(command.OperationId, alterId, invalidAlterIdEntityRef);

    public static async Task<CommandExecutionResult<AlterCommandResult>?> RejectIfAlterNotFoundAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        IAlterRepository alterRepository,
        AlterId alterId,
        EntityRef missingAlterEntityRef,
        CancellationToken cancellationToken = default)
    {
        var exists = await alterRepository.ExistsAsync(command.PrincipalId, alterId, cancellationToken);
        return exists
            ? null
            : CommandHandler.RejectInvariant<AlterCommandResult>(command.OperationId, missingAlterEntityRef);
    }

    public static async Task<CommandExecutionResult<AlterCommandResult>> ExecuteCreateAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        Func<CancellationToken, Task<AlterId?>> createAsync,
        EntityRef createFailedEntityRef,
        Func<AlterId, CancellationToken, ValueTask> publishAccepted,
        CancellationToken cancellationToken = default)
    {
        var createdAlterId = await createAsync(cancellationToken);
        if (createdAlterId is null)
            return CommandHandler.RejectInvariant<AlterCommandResult>(command.OperationId, createFailedEntityRef);

        await publishAccepted(createdAlterId.Value, cancellationToken);
        return Success(command.PrincipalId, createdAlterId.Value);
    }

    public static CommandExecutionResult<AlterCommandResult>? RejectIfMutationFailed<TCommand>(
        CommandEnvelope<TCommand> command,
        bool succeeded,
        EntityRef failedEntityRef)
        => !succeeded
            ? CommandHandler.RejectInvariant<AlterCommandResult>(command.OperationId, failedEntityRef)
            : null;

    public static async Task<CommandExecutionResult<AlterCommandResult>> ExecuteMutationAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        AlterId alterId,
        Func<CancellationToken, Task<bool>> mutateAsync,
        EntityRef failedEntityRef,
        Func<CancellationToken, ValueTask> publishAccepted,
        CancellationToken cancellationToken = default)
    {
        var succeeded = await mutateAsync(cancellationToken);
        if (RejectIfMutationFailed(command, succeeded, failedEntityRef) is { } mutationReject)
            return mutationReject;

        await publishAccepted(cancellationToken);
        return Success(command.PrincipalId, alterId);
    }
}
