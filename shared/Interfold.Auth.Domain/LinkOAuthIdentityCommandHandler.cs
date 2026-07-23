using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;

namespace Interfold.Shared.Domain.Auth;

public sealed record LinkOAuthIdentityCommandResult(AccountLinkResult Result, ScopedSystemId? SystemId);

public sealed class LinkOAuthIdentityCommandHandler : ICommandHandler<LinkOAuthIdentityCommand, LinkOAuthIdentityCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IClusterEventBus _eventBus;

    public LinkOAuthIdentityCommandHandler(
        IAccountRepository accountRepository,
        IClusterEventBus eventBus)
    {
        _accountRepository = accountRepository;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<LinkOAuthIdentityCommandResult>> HandleAsync(CommandEnvelope<LinkOAuthIdentityCommand> command, CancellationToken cancellationToken = default)
    {
        var linkToken = command.Payload.LinkToken;
        var identity = command.Payload.Identity;

        var resolvedSystemId = await _accountRepository.ResolveSystemIdByLinkTokenAsync(linkToken, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolvedSystemId?.Value) || !ScopedSystemId.TryParseScoped(resolvedSystemId.Value, out var systemId))
            return CommandHandler.RejectInvariant<LinkOAuthIdentityCommandResult>(command.OperationId, EntityRefs.AuthLinkInvalidToken);

        await _accountRepository.ClearLinkTokenAsync(systemId, cancellationToken);

        var result = await _accountRepository.LinkIdentityToUserAsync(systemId, identity, cancellationToken);

        if (result == AccountLinkResult.Success)
        {
            switch (identity)
            {
                case { Discord: { } discordId }:
                    await _eventBus.PublishAsync(
                        new SettingsDiscordAccountLinkedEvent(systemId, discordId),
                        cancellationToken);
                    break;
                case { Google: { } email }:
                    await _eventBus.PublishAsync(
                        new SettingsGoogleAccountLinkedEvent(systemId, email),
                        cancellationToken);
                    break;
                case { Apple: { } appleId }:
                    await _eventBus.PublishAsync(
                        new SettingsAppleAccountLinkedEvent(systemId, appleId),
                        cancellationToken);
                    break;
            }
        }

        return CommandExecutionResult<LinkOAuthIdentityCommandResult>.Success(new LinkOAuthIdentityCommandResult(result, systemId));
    }
}
