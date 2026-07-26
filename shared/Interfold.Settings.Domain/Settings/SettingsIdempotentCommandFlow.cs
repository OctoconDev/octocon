using Interfold.Settings.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Settings.Domain.Settings;

/// <summary>Mutate → check → publish → success helper for settings handlers that already
/// inherit <see cref="IdempotentCommandHandler{TPayload,TResult}"/>. Companion to
/// <see cref="SettingsFieldCommandFlow"/> (which returns a different result shape).</summary>
internal static class SettingsIdempotentCommandFlow
{
    public static CommandExecutionResult<SettingsCommandResult> Success(
        ScopedSystemId systemId,
        SettingsAction action)
        => CommandExecutionResult<SettingsCommandResult>.Success(new SettingsCommandResult(systemId, action, Replay: false));

    public static CommandExecutionResult<SettingsCommandResult>? RejectIfMutationFailed<TCommand>(
        CommandEnvelope<TCommand> command,
        bool succeeded,
        EntityRef failedEntityRef)
        => !succeeded
            ? CommandHandler.RejectInvariant<SettingsCommandResult>(command.OperationId, failedEntityRef)
            : null;

    public static async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteMutationAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        Func<CancellationToken, Task<bool>> mutateAsync,
        EntityRef failedEntityRef,
        SettingsAction action,
        Func<CancellationToken, ValueTask>? publishAccepted = null,
        CancellationToken cancellationToken = default)
    {
        var succeeded = await mutateAsync(cancellationToken);
        if (RejectIfMutationFailed(command, succeeded, failedEntityRef) is { } reject)
            return reject;

        if (publishAccepted is not null)
            await publishAccepted(cancellationToken);

        return Success(command.PrincipalId, action);
    }

    public static async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteApplyAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        Func<CancellationToken, Task> applyAsync,
        SettingsAction action,
        Func<CancellationToken, ValueTask>? publishAccepted = null,
        CancellationToken cancellationToken = default)
    {
        await applyAsync(cancellationToken);

        if (publishAccepted is not null)
            await publishAccepted(cancellationToken);

        return Success(command.PrincipalId, action);
    }
}
