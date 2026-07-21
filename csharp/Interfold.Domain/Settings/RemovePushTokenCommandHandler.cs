using Interfold.Contracts;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Settings;

public sealed class RemovePushTokenCommandHandler : IdempotentCommandHandler<RemovePushTokenCommand, SettingsCommandResult>
{
    private readonly INotificationTokenRepository _repository;

    public RemovePushTokenCommandHandler(
        INotificationTokenRepository repository,
        IIdempotencyStore idempotencyStore)
:base(idempotencyStore)    {
        _repository = repository;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsPushTokenRemove;

protected override async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<RemovePushTokenCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (RejectIfBlank(command, command.Payload.Token.Value, EntityRefs.SettingsPushTokenInvalid) is { } blankReject)
            return blankReject;

        // Removal is treated as idempotent for retry safety.
        return await SettingsIdempotentCommandFlow.ExecuteApplyAsync(
            command,
            ct => _repository.RemoveAsync(new(command.Payload.Token.Value.Trim()), ct),
            SettingsAction.PushTokenRemoved,
            cancellationToken: cancellationToken);
    }

}
