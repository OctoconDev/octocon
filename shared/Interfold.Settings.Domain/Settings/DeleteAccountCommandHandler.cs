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

public sealed class DeleteAccountCommandHandler : IdempotentCommandHandler<DeleteAccountCommand, SettingsCommandResult>
{
    private readonly IClusterEventBus _eventBus;
    private readonly IAccountRepository _accountRepository;
    private readonly IAlterRepository _alterRepository;
    private readonly ITagRepository _tagRepository;
    private readonly IPollRepository _pollRepository;
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IJournalRepository _journalRepository;
    private readonly IFrontingRepository _frontingRepository;
    private readonly IFriendshipRepository _friendshipRepository;

    public DeleteAccountCommandHandler(
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus,
        IAccountRepository accountRepository,
        IAlterRepository alterRepository,
        ITagRepository tagRepository,
        IPollRepository pollRepository,
        ISettingsFieldRepository fieldRepository,
        IJournalRepository journalRepository,
        IFrontingRepository frontingRepository,
        IFriendshipRepository friendshipRepository)
        : base(idempotencyStore)
    {
        _eventBus = eventBus;
        _accountRepository = accountRepository;
        _alterRepository = alterRepository;
        _tagRepository = tagRepository;
        _pollRepository = pollRepository;
        _fieldRepository = fieldRepository;
        _journalRepository = journalRepository;
        _frontingRepository = frontingRepository;
        _friendshipRepository = friendshipRepository;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsAccountDelete;

    protected override Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<DeleteAccountCommand> command,
        CancellationToken cancellationToken)
    {
        // Shared between the mutation and publish lambdas — the friendship repo returns the
        // set of counterparties whose Friendship rows we tore down, and each one needs a
        // FriendshipRemovedEvent on the cluster bus so their sockets refresh their
        // friend-list. IdempotentCommandHandler skips ExecuteCoreAsync on replay, so this
        // list is only ever populated once per idempotency key — same guard the previous
        // ExecuteAndPublishAsync gave us via `Result.Replay: false`.
        var unfriendedIds = new List<SystemId>();

        return SettingsIdempotentCommandFlow.ExecuteMutationAsync(
            command,
            async ct =>
            {
                var systemId = command.PrincipalId;

                var alters = await _alterRepository.ListAsync(systemId, ct);
                foreach (var alter in alters)
                {
                    await _alterRepository.DeleteAsync(systemId, alter.Id, ct);
                    //TODO: Delete alter image if it exists
                }

                var tags = await _tagRepository.ListAsync(systemId, ct);
                foreach (var tag in tags)
                {
                    await _tagRepository.DeleteAsync(systemId, tag.Id, ct);
                }

                var polls = await _pollRepository.ListAsync(systemId, ct);
                foreach (var poll in polls)
                {
                    await _pollRepository.DeleteAsync(systemId, poll.Id, ct);
                }

                var fields = await _fieldRepository.ListAsync(systemId, ct);
                foreach (var field in fields)
                {
                    await _fieldRepository.DeleteAsync(systemId, field.Id, ct);
                }

                var globalEntries = await _journalRepository.ListGlobalAsync(systemId, ct);
                foreach (var entry in globalEntries)
                {
                    await _journalRepository.DeleteGlobalAsync(systemId, entry.Id, ct);
                }

                // Fronting records are tied to alters so no need to delete them here

                var deletedIds = await _friendshipRepository.DeleteAllForSystemAsync(systemId, ct);
                unfriendedIds.AddRange(deletedIds);

                return await _accountRepository.DeleteAsync(systemId, ct);

                //TODO: Delete account image if it exists
            },
            EntityRefs.SettingsActionFailed(SettingsAction.AccountDeleted),
            SettingsAction.AccountDeleted,
            async ct =>
            {
                await _eventBus.PublishAsync(new SettingsAccountDeletedSignalEvent(command.PrincipalId), ct);

                foreach (var friendId in unfriendedIds)
                {
                    // The friendship repos return bare (region-stripped) ids in their DeleteAll
                    // results — compose with the principal's region so the socket router filter
                    // sees a scoped TargetSystemId. Cross-region friendships would want the
                    // friend's own region here; a same-region compose is the current behaviour.
                    var friendTarget = ScopedSystemId.Compose(
                        command.PrincipalId.Region,
                        friendId);
                    await _eventBus.PublishAsync(new FriendshipRemovedEvent(friendTarget, command.PrincipalId), ct);
                }
            },
            cancellationToken);
    }
}
