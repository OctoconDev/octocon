using Interfold.Polls.Contracts;
using Interfold.Polls.Contracts.Events;
using Interfold.Polls.Contracts.Ids;
using Interfold.Polls.Contracts.Models.Commands;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Polls.Domain;

internal static class PollCommandFlow
{
    public static CommandExecutionResult<PollCommandResult> Success(SystemId systemId, PollId pollId)
        => CommandExecutionResult<PollCommandResult>.Success(new PollCommandResult(systemId, pollId, Replay: false));

    public static async Task<CommandExecutionResult<PollCommandResult>> ExecuteCreateAsync(
        CommandEnvelope<CreatePollCommand> command,
        IClusterEventBus eventBus,
        Func<CancellationToken, Task<PollId?>> create,
        CancellationToken cancellationToken = default)
    {
        var pollId = await create(cancellationToken);
        if (pollId is null)
            return CommandHandler.RejectInvariant<PollCommandResult>(command.OperationId, EntityRefs.PollCreateFailed);

        await eventBus.PublishAsync(new PollCreatedEvent(command.PrincipalId, pollId.Value), cancellationToken);
        return Success(command.PrincipalId, pollId.Value);
    }

    public static Task<CommandExecutionResult<PollCommandResult>> ExecuteUpdateAsync(
        CommandEnvelope<UpdatePollCommand> command,
        IPollRepository pollRepository,
        IClusterEventBus eventBus,
        CancellationToken cancellationToken = default)
        => ExecuteExistingPollMutationAsync(
            command,
            command.Payload.Id,
            pollRepository,
            ct => pollRepository.UpdateAsync(command.PrincipalId, command.Payload, ct),
            ct => eventBus.PublishAsync(new PollUpdatedEvent(command.PrincipalId, command.Payload.Id), ct),
            EntityRefs.PollUpdateFailed,
            cancellationToken);

    public static Task<CommandExecutionResult<PollCommandResult>> ExecuteDeleteAsync(
        CommandEnvelope<DeletePollCommand> command,
        IPollRepository pollRepository,
        IClusterEventBus eventBus,
        CancellationToken cancellationToken = default)
        => ExecuteExistingPollMutationAsync(
            command,
            command.Payload.PollId,
            pollRepository,
            ct => pollRepository.DeleteAsync(command.PrincipalId, command.Payload.PollId, ct),
            ct => eventBus.PublishAsync(new PollDeletedEvent(command.PrincipalId, command.Payload.PollId), ct),
            EntityRefs.PollDeleteFailed,
            cancellationToken);

    // NOTE: mutateAsync and publishAccepted are DEFERRED (Func) rather than hot Task/ValueTask.
    // The mutate must not execute unless the poll actually exists (otherwise a stray UPDATE/DELETE
    // fires against the DB), and the publish must only fire on the accepted branch (otherwise
    // downstream observers see events for polls that were never mutated). Both must also be
    // observed so that any exceptions surface on the request path instead of becoming
    // TaskScheduler.UnobservedTaskException. Passing hot awaitables here loses all three
    // guarantees — see PollCommandFlowExecutionTests for the regression guards.
    private static async Task<CommandExecutionResult<PollCommandResult>> ExecuteExistingPollMutationAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        PollId pollId,
        IPollRepository pollRepository,
        Func<CancellationToken, Task<bool>> mutateAsync,
        Func<CancellationToken, ValueTask> publishAccepted,
        EntityRef mutationFailedEntityRef,
        CancellationToken cancellationToken)
    {
        var exists = await pollRepository.ExistsAsync(command.PrincipalId, pollId, cancellationToken);
        if (!exists)
            return CommandHandler.RejectInvariant<PollCommandResult>(command.OperationId, EntityRefs.PollNotFound);

        var changed = await mutateAsync(cancellationToken);
        if (!changed)
            return CommandHandler.RejectInvariant<PollCommandResult>(command.OperationId, mutationFailedEntityRef);

        await publishAccepted(cancellationToken);
        return Success(command.PrincipalId, pollId);
    }
}
