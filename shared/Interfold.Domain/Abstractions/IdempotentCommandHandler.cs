using Interfold.Contracts;
using Interfold.Contracts.Models;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Abstractions;

public abstract class IdempotentCommandHandler<TPayload, TResult> : ICommandHandler<TPayload, TResult>
    where TResult : class, ICommandResult<TResult>
{
    private readonly IIdempotencyStore _idempotencyStore;

    protected IdempotentCommandHandler(IIdempotencyStore idempotencyStore)
    {
        _idempotencyStore = idempotencyStore;
    }

    public async Task<CommandExecutionResult<TResult>> HandleAsync(
        CommandEnvelope<TPayload> command,
        CancellationToken cancellationToken = default
    )
    {
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
                return RejectDuplicate(command);
            }

            var replay = CommandSerialization.Deserialize<TResult>(previous.OutcomePayload);
            if (replay is not null)
            {
                return CommandExecutionResult<TResult>.Success(CreateReplayResult(replay));
            }
        }

        var execution = await ExecuteCoreAsync(command, cancellationToken);

        if (execution.Accepted && execution.Result is not null)
        {
            var resultJson = CommandSerialization.Serialize(execution.Result);

            await _idempotencyStore.SaveAsync(
                command.PrincipalId,
                command.OperationId,
                command.IdempotencyKey,
                payloadHash,
                CommandSerialization.Hash(resultJson),
                resultJson,
                cancellationToken
            );
        }

        return execution;
    }

    protected abstract Task<CommandExecutionResult<TResult>> ExecuteCoreAsync(CommandEnvelope<TPayload> command, CancellationToken cancellationToken);

    /// <summary>
    /// Produces the replayed-result variant when an idempotent hit is served from the store.
    /// The default flips the <c>Replay</c> flag via <see cref="ICommandResult{TSelf}.WithReplay"/>;
    /// override only if a specific result record needs cross-field invariants (e.g. clearing
    /// a one-shot side-effect handle) to move together with the flag. Every replay path here
    /// used to be a 2-line handler override that read <c>originalResult with { Replay = true }</c>;
    /// the F-bounded interface plus this virtual default deletes 46 near-identical overrides.
    /// </summary>
    protected virtual TResult CreateReplayResult(TResult originalResult) =>
        originalResult.WithReplay();

    protected abstract EntityRef DuplicateEntityRef { get; }

    private CommandExecutionResult<TResult> RejectDuplicate(CommandEnvelope<TPayload> command) =>
        CommandExecutionResult<TResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictDuplicate,
                command.OperationId,
                DuplicateEntityRef,
                ResolutionHint.NoRetry
            )
        );

    protected static CommandExecutionResult<TResult> RejectInvariant(
        CommandEnvelope<TPayload> command,
        EntityRef entityRef
    ) =>
        CommandHandler.RejectInvariant<TResult>(command.OperationId, entityRef);

    protected static CommandExecutionResult<TResult>? RejectIfBlank(
        CommandEnvelope<TPayload> command,
        string? value,
        EntityRef entityRef
    ) =>
        CommandHandler.RejectIfBlank<TResult>(command.OperationId, value, entityRef);

    protected static CommandExecutionResult<TResult>? RejectIfAlterIdOutOfRange(
        CommandEnvelope<TPayload> command,
        AlterId alterId,
        EntityRef entityRef
    ) =>
        CommandHandler.RejectIfAlterIdOutOfRange<TResult>(command.OperationId, alterId, entityRef);
}

public static class CommandHandler
{
    public static CommandExecutionResult<TResult> RejectInvariant<TResult>(
        OperationId operationId,
        EntityRef entityRef
    ) =>
        CommandExecutionResult<TResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictInvariant,
                operationId,
                entityRef,
                ResolutionHint.ManualMergeRequired
            )
        );

    public static CommandExecutionResult<TResult>? RejectIfBlank<TResult>(
        OperationId operationId,
        string? value,
        EntityRef entityRef
    ) =>
        string.IsNullOrWhiteSpace(value) ? RejectInvariant<TResult>(operationId, entityRef) : null;

    public static CommandExecutionResult<TResult>? RejectIfAlterIdOutOfRange<TResult>(
        OperationId operationId,
        AlterId alterId,
        EntityRef entityRef
    ) =>
        alterId.Value < 1 ? RejectInvariant<TResult>(operationId, entityRef) : null;
}
