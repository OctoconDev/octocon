using Interfold.Contracts;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Settings;

/// <summary>Field-creation helper for handlers returning
/// <see cref="SettingsFieldCommandResult"/> (carries the created <see cref="FieldId"/>);
/// contrasts with <see cref="SettingsIdempotentCommandFlow"/> which returns a plain
/// <see cref="SettingsCommandResult"/>.</summary>
internal static class SettingsFieldCommandFlow
{
    public static CommandExecutionResult<SettingsFieldCommandResult> Success(
        ScopedSystemId systemId,
        SettingsFieldAction action,
        FieldId fieldId)
        => CommandExecutionResult<SettingsFieldCommandResult>.Success(
            new SettingsFieldCommandResult(systemId, action, fieldId, Replay: false));

    public static async Task<CommandExecutionResult<SettingsFieldCommandResult>> ExecuteCreateAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        SettingsFieldAction action,
        EntityRef createFailedEntityRef,
        Func<CancellationToken, Task<FieldId?>> createAsync,
        Func<CancellationToken, ValueTask> publishAccepted,
        CancellationToken cancellationToken = default)
    {
        var fieldId = await createAsync(cancellationToken);
        if (fieldId is null)
            return CommandHandler.RejectInvariant<SettingsFieldCommandResult>(command.OperationId, createFailedEntityRef);

        await publishAccepted(cancellationToken);
        return Success(command.PrincipalId, action, fieldId.Value);
    }
}
