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
