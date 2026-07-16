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

public sealed class WipeTagsCommandHandler : ICommandHandler<WipeTagsCommand, SettingsCommandResult>
{
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;
    private readonly ITagRepository _tagRepository;

    public WipeTagsCommandHandler(
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus,
        ITagRepository tagRepository)
    {
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
        _tagRepository = tagRepository;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<WipeTagsCommand> command, CancellationToken cancellationToken = default)
    {
        var result = await SettingsCommandHelper.ExecuteAsync(command, SettingsAction.TagsWiped, EntityRefs.SettingsTagsWipe, _idempotencyStore, async ct =>
        {
            var systemId = command.PrincipalId;
            var tags = await _tagRepository.ListAsync(systemId, ct);
            foreach (var tag in tags)
            {
                // Repository delete also removes alter_tag join rows in both backends, mirroring
                // Octocon.Accounts.wipe_tags/1 in the legacy stack which truncated both Tag and
                // AlterTag tables for the system.
                await _tagRepository.DeleteAsync(systemId, tag.Id, ct);
            }

            return true;
        }, cancellationToken);

        if (result is { Accepted: true, Result.Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsTagsWipedSignalEvent(command.PrincipalId), cancellationToken);
        }

        return result;
    }
}
