using Interfold.Settings.Contracts;
using Interfold.Settings.Contracts.Events;
using Interfold.Settings.Contracts.Models.Commands;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Settings.Domain.Settings;

public sealed class UnlinkAppleCommandHandler : IdempotentCommandHandler<UnlinkAppleCommand, SettingsCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IClusterEventBus _eventBus;

    public UnlinkAppleCommandHandler(IAccountRepository accountRepository, IIdempotencyStore idempotencyStore, IClusterEventBus eventBus)
        : base(idempotencyStore)
    {
        _accountRepository = accountRepository;
        _eventBus = eventBus;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsUnlinkApple;

    protected override Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<UnlinkAppleCommand> command,
        CancellationToken cancellationToken)
        => SettingsIdempotentCommandFlow.ExecuteMutationAsync(
            command,
            ct => _accountRepository.UnlinkAppleAsync(command.PrincipalId, ct),
            EntityRefs.SettingsActionFailed(SettingsAction.AppleUnlinked),
            SettingsAction.AppleUnlinked,
            ct => _eventBus.PublishAsync(new SettingsAppleAccountUnlinkedSignalEvent(command.PrincipalId), ct),
            cancellationToken);
}
