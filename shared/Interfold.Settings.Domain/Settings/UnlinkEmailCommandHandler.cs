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
            EntityRefs.SettingsActionFailed(SettingsAction.EmailUnlinked),
            SettingsAction.EmailUnlinked,
            // Legacy Octocon.Accounts.unlink_email_from_user broadcast google_account_unlinked
            // because the only email auth path in the old stack was the Google OAuth one;
            // mirror that contract so existing clients keep receiving the same socket signal.
            ct => _eventBus.PublishAsync(new SettingsGoogleAccountUnlinkedSignalEvent(command.PrincipalId), ct),
            cancellationToken);
}
