using Interfold.Journals.Contracts;
using Interfold.Journals.Contracts.Events;
using Interfold.Journals.Contracts.Ids;
using Interfold.Journals.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Journals.Domain;

internal static class GlobalJournalCommandFlow
{
    public static CommandExecutionResult<GlobalJournalCommandResult> Success(
        ScopedSystemId systemId,
        EntryId entryId)
        => CommandExecutionResult<GlobalJournalCommandResult>.Success(new GlobalJournalCommandResult(systemId, entryId, Replay: false));

    public static CommandExecutionResult<GlobalJournalCommandResult>? RejectIfMutationFailed<TCommand>(
        CommandEnvelope<TCommand> command,
        bool succeeded,
        EntityRef failureEntityRef)
        => !succeeded
            ? CommandHandler.RejectInvariant<GlobalJournalCommandResult>(command.OperationId, failureEntityRef)
            : null;

    public static async Task<CommandExecutionResult<GlobalJournalCommandResult>?> ExecuteMutationOrRejectAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        Func<CancellationToken, Task<bool>> mutateAsync,
        EntityRef failureEntityRef,
        CancellationToken cancellationToken = default)
    {
        var mutated = await mutateAsync(cancellationToken);
        return RejectIfMutationFailed(command, mutated, failureEntityRef);
    }

    public static async Task<CommandExecutionResult<GlobalJournalCommandResult>> ExecuteCreateAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        IJournalRepository journalRepository,
        IClusterEventBus eventBus,
        Func<CancellationToken, Task<EntryId?>> createAsync,
        EntityRef failureEntityRef,
        CancellationToken cancellationToken = default)
    {
        var entryId = await createAsync(cancellationToken);
        if (entryId is null)
            return CommandHandler.RejectInvariant<GlobalJournalCommandResult>(command.OperationId, failureEntityRef);

        await eventBus.PublishAsync(new GlobalJournalEntryCreatedEvent(command.PrincipalId, entryId.Value), cancellationToken);
        return Success(command.PrincipalId, entryId.Value);
    }

    public static async Task<CommandExecutionResult<GlobalJournalCommandResult>> ExecuteUpdateAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        EntryId entryId,
        IJournalRepository journalRepository,
        IClusterEventBus eventBus,
        Func<CancellationToken, Task<bool>> applyAsync,
        EntityRef failureEntityRef,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteExistingEntryMutationAsync(
            command,
            entryId,
            journalRepository,
            applyAsync,
            failureEntityRef,
            ct => eventBus.PublishAsync(new GlobalJournalEntryUpdatedEvent(command.PrincipalId, entryId), ct),
            cancellationToken);
    }

    public static async Task<CommandExecutionResult<GlobalJournalCommandResult>> ExecuteDeleteAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        EntryId entryId,
        IJournalRepository journalRepository,
        IClusterEventBus eventBus,
        Func<CancellationToken, Task<bool>> deleteAsync,
        EntityRef failureEntityRef,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteExistingEntryMutationAsync(
            command,
            entryId,
            journalRepository,
            deleteAsync,
            failureEntityRef,
            ct => eventBus.PublishAsync(new GlobalJournalEntryDeletedEvent(command.PrincipalId, entryId), ct),
            cancellationToken);
    }

    private static async Task<CommandExecutionResult<GlobalJournalCommandResult>> ExecuteExistingEntryMutationAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        EntryId entryId,
        IJournalRepository journalRepository,
        Func<CancellationToken, Task<bool>> mutateAsync,
        EntityRef failureEntityRef,
        Func<CancellationToken, ValueTask> publishAsync,
        CancellationToken cancellationToken)
    {
        var exists = await journalRepository.ExistsGlobalAsync(command.PrincipalId, entryId, cancellationToken);
        if (!exists)
            return CommandHandler.RejectInvariant<GlobalJournalCommandResult>(command.OperationId, EntityRefs.JournalNotFound);

        if (await ExecuteMutationOrRejectAsync(command, mutateAsync, failureEntityRef, cancellationToken) is { } mutationReject)
            return mutationReject;

        await publishAsync(cancellationToken);
        return Success(command.PrincipalId, entryId);
    }
}