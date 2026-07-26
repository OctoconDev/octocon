using Interfold.Friendships.Contracts;
using Interfold.Friendships.Contracts.Enums;
using Interfold.Friendships.Contracts.Models.Read;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Friendships.Domain;

internal static class FriendshipCommandFlow
{
    public static CommandExecutionResult<FriendshipCommandResult> Success(
        SystemId principalId,
        SystemId peerSystemId,
        FriendshipAction action)
        => CommandExecutionResult<FriendshipCommandResult>.Success(
            new FriendshipCommandResult(principalId, peerSystemId, action, Replay: false));

    public static CommandExecutionResult<FriendshipCommandResult>? RejectIfMutationOutcomeFailed<TCommand>(
        CommandEnvelope<TCommand> command,
        FriendRequestMutationOutcome outcome)
    {
        if (outcome.ToRejectionEntityRef() is not { } rejectionEntityRef)
            return null;

        return CommandHandler.RejectInvariant<FriendshipCommandResult>(command.OperationId, rejectionEntityRef);
    }

    public static CommandExecutionResult<FriendshipCommandResult>? RejectIfMutationFailed<TCommand>(
        CommandEnvelope<TCommand> command,
        bool succeeded,
        EntityRef failedEntityRef)
        => !succeeded
            ? CommandHandler.RejectInvariant<FriendshipCommandResult>(command.OperationId, failedEntityRef)
            : null;

    public static async Task<CommandExecutionResult<FriendshipCommandResult>?> ExecuteMutationOrRejectAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        Func<CancellationToken, Task<bool>> mutateAsync,
        EntityRef failedEntityRef,
        CancellationToken cancellationToken = default)
    {
        var succeeded = await mutateAsync(cancellationToken);
        return RejectIfMutationFailed(command, succeeded, failedEntityRef);
    }
}
