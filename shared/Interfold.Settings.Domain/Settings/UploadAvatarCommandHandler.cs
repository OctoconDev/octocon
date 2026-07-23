using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Domain.Settings;

public sealed class UploadAvatarCommandHandler : IdempotentCommandHandler<UploadAvatarCommand, SettingsCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IClusterEventBus _eventBus;

    public UploadAvatarCommandHandler(IAccountRepository accountRepository, IIdempotencyStore idempotencyStore, IClusterEventBus eventBus)
        : base(idempotencyStore)
    {
        _accountRepository = accountRepository;
        _eventBus = eventBus;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsAvatarUpload;

    protected override Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<UploadAvatarCommand> command,
        CancellationToken cancellationToken)
    {
        if (RejectIfBlank(command, command.Payload.AvatarUrl, EntityRefs.SettingsAvatarInvalid) is { } blankReject)
            return Task.FromResult(blankReject);

        return SettingsIdempotentCommandFlow.ExecuteMutationAsync(
            command,
            ct => _accountRepository.UpdateAvatarAsync(command.PrincipalId, command.Payload.AvatarUrl, command.Payload.Source, ct),
            EntityRefs.SettingsActionFailed(SettingsAction.AvatarUploaded),
            SettingsAction.AvatarUploaded,
            ct => _eventBus.PublishProfileUpdatedAsync(command.PrincipalId, includeUsername: false, ct),
            cancellationToken);
    }
}
