using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Settings;

public sealed class UnlinkDiscordCommandHandler : IdempotentCommandHandler<UnlinkDiscordCommand, SettingsCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IClusterEventBus _eventBus;

    public UnlinkDiscordCommandHandler(
        IAccountRepository accountRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
        : base(idempotencyStore)
    {
        _accountRepository = accountRepository;
        _eventBus = eventBus;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsUnlinkDiscord;

    protected override Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<UnlinkDiscordCommand> command,
        CancellationToken cancellationToken)
        => SettingsIdempotentCommandFlow.ExecuteMutationAsync(
            command,
            ct => _accountRepository.UnlinkDiscordAsync(command.PrincipalId, ct),
            EntityRefs.SettingsActionFailed(SettingsAction.DiscordUnlinked),
            SettingsAction.DiscordUnlinked,
            ct => _eventBus.PublishAsync(new SettingsDiscordAccountUnlinkedSignalEvent(command.PrincipalId), ct),
            cancellationToken);
}
