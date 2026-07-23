using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Journals;

internal static class AlterJournalCommandFlow
{
    public static CommandExecutionResult<AlterJournalCommandResult> Success(
        ScopedSystemId systemId,
        EntryId entryId,
        AlterId alterId)
        => CommandExecutionResult<AlterJournalCommandResult>.Success(
            new AlterJournalCommandResult(systemId, entryId, alterId, Replay: false));

    public static async Task<CommandExecutionResult<TResult>?> RejectIfInvalidOrMissingAlterAsync<TCommand, TResult>(
        CommandEnvelope<TCommand> command,
        AlterId alterId,
        IAlterExistenceCheck alterExistence,
        EntityRef invalidAlterIdEntityRef,
        EntityRef missingAlterEntityRef,
        CancellationToken cancellationToken = default)
    {
        if (CommandHandler.RejectIfAlterIdOutOfRange<TResult>(command.OperationId, alterId, invalidAlterIdEntityRef) is { } invalidAlterIdReject)
            return invalidAlterIdReject;

        var alterExists = await alterExistence.ExistsAsync(command.PrincipalId, alterId, cancellationToken);
        return alterExists
            ? null
            : CommandHandler.RejectInvariant<TResult>(command.OperationId, missingAlterEntityRef);
    }

    public static CommandExecutionResult<AlterJournalCommandResult>? RejectIfMutationFailed<TCommand>(
        CommandEnvelope<TCommand> command,
        bool succeeded,
        EntityRef failureEntityRef)
        => !succeeded
            ? CommandHandler.RejectInvariant<AlterJournalCommandResult>(command.OperationId, failureEntityRef)
            : null;

    public static async Task<CommandExecutionResult<AlterJournalCommandResult>?> ExecuteMutationOrRejectAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        Func<CancellationToken, Task<bool>> mutateAsync,
        EntityRef failureEntityRef,
        CancellationToken cancellationToken = default)
    {
        var mutated = await mutateAsync(cancellationToken);
        return RejectIfMutationFailed(command, mutated, failureEntityRef);
    }

    public static async Task<CommandExecutionResult<AlterJournalCommandResult>> ExecuteCreateAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        IClusterEventBus eventBus,
        Func<CancellationToken, Task<EntryId?>> createAsync,
        AlterId alterId,
        EntityRef failureEntityRef,
        CancellationToken cancellationToken = default)
    {
        var entryId = await createAsync(cancellationToken);
        if (entryId is null)
            return CommandHandler.RejectInvariant<AlterJournalCommandResult>(command.OperationId, failureEntityRef);

        await eventBus.PublishAsync(new AlterJournalEntryCreatedEvent(command.PrincipalId, entryId.Value), cancellationToken);
        return Success(command.PrincipalId, entryId.Value, alterId);
    }

    public static async Task<CommandExecutionResult<AlterJournalCommandResult>> ExecuteExistingEntryMutationAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        EntryId entryId,
        IJournalRepository journalRepository,
        Func<CancellationToken, Task<bool>> mutateAsync,
        EntityRef failureEntityRef,
        Func<CancellationToken, ValueTask> publishAsync,
        CancellationToken cancellationToken = default)
    {
        var alterRef = await journalRepository.GetAlterRefAsync(command.PrincipalId, entryId, cancellationToken);
        if (alterRef is null)
            return CommandHandler.RejectInvariant<AlterJournalCommandResult>(command.OperationId, EntityRefs.JournalNotFound);

        if (await ExecuteMutationOrRejectAsync(command, mutateAsync, failureEntityRef, cancellationToken) is { } mutationReject)
            return mutationReject;

        await publishAsync(cancellationToken);
        return Success(command.PrincipalId, entryId, alterRef.AlterId);
    }
}