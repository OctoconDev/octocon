using Interfold.Contracts;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Settings;

public sealed class AddPushTokenCommandHandler : IdempotentCommandHandler<AddPushTokenCommand, SettingsCommandResult>
{
    private readonly INotificationTokenRepository _repository;

    public AddPushTokenCommandHandler(
        INotificationTokenRepository repository,
        IIdempotencyStore idempotencyStore)
:base(idempotencyStore)    {
        _repository = repository;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsPushTokenAdd;

protected override async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<AddPushTokenCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (RejectIfBlank(command, command.Payload.Token.Value, EntityRefs.SettingsPushTokenInvalid) is { } blankReject)
            return blankReject;

        return await SettingsIdempotentCommandFlow.ExecuteMutationAsync(
            command,
            ct => _repository.AddAsync(command.PrincipalId, new(command.Payload.Token.Value.Trim()), ct),
            EntityRefs.SettingsPushTokenAddFailed,
            SettingsAction.PushTokenAdded,
            cancellationToken: cancellationToken);
    }

}
