using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Settings;

public sealed class UnlinkEmailCommandHandler : ICommandHandler<UnlinkEmailCommand, SettingsCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public UnlinkEmailCommandHandler(
        IAccountRepository accountRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _accountRepository = accountRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<UnlinkEmailCommand> command, CancellationToken cancellationToken = default)
    {
        var result = await SettingsCommandHelper.ExecuteAsync(
            command,
            SettingsAction.EmailUnlinked,
            EntityRefs.SettingsUnlinkEmail,
            _idempotencyStore,
            ct => _accountRepository.UnlinkEmailAsync(command.PrincipalId, ct),
            cancellationToken);

        if (result is { Accepted: true, Result.Replay: false })
        {
            // Legacy Octocon.Accounts.unlink_email_from_user broadcast google_account_unlinked
            // because the only email auth path in the old stack was the Google OAuth one;
            // mirror that contract so existing clients keep receiving the same socket signal.
            await _eventBus.PublishAsync(new SettingsGoogleAccountUnlinkedSignalEvent(command.PrincipalId), cancellationToken);
        }

        return result;
    }
}