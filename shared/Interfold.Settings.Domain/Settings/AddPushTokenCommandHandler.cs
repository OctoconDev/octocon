using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

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
