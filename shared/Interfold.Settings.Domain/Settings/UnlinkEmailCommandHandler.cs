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

public sealed class UnlinkEmailCommandHandler : IdempotentCommandHandler<UnlinkEmailCommand, SettingsCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IClusterEventBus _eventBus;

    public UnlinkEmailCommandHandler(
        IAccountRepository accountRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
        : base(idempotencyStore)
    {
        _accountRepository = accountRepository;
        _eventBus = eventBus;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsUnlinkEmail;

    protected override Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<UnlinkEmailCommand> command,
        CancellationToken cancellationToken)
        => SettingsIdempotentCommandFlow.ExecuteMutationAsync(
            command,
            ct => _accountRepository.UnlinkEmailAsync(command.PrincipalId, ct),
            SettingsAction.EmailUnlinked.ToFailedEntityRef(),
            SettingsAction.EmailUnlinked,
            // Legacy Octocon.Accounts.unlink_email_from_user broadcast google_account_unlinked
            // because the only email auth path in the old stack was the Google OAuth one;
            // mirror that contract so existing clients keep receiving the same socket signal.
            ct => _eventBus.PublishAsync(new SettingsGoogleAccountUnlinkedSignalEvent(command.PrincipalId), ct),
            cancellationToken);
}
