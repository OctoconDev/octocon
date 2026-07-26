using Interfold.Settings.Contracts;
using Interfold.Settings.Contracts.Events;
using Interfold.Settings.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Tags.Domain.Abstractions.Repository;

namespace Interfold.Settings.Domain.Settings;

public sealed class WipeTagsCommandHandler : IdempotentCommandHandler<WipeTagsCommand, SettingsCommandResult>
{
    private readonly IClusterEventBus _eventBus;
    private readonly ITagRepository _tagRepository;

    public WipeTagsCommandHandler(
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus,
        ITagRepository tagRepository)
        : base(idempotencyStore)
    {
        _eventBus = eventBus;
        _tagRepository = tagRepository;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsTagsWipe;

    protected override Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<WipeTagsCommand> command,
        CancellationToken cancellationToken)
        => SettingsIdempotentCommandFlow.ExecuteMutationAsync(
            command,
            async ct =>
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
            },
            EntityRefs.SettingsActionFailed(SettingsAction.TagsWiped),
            SettingsAction.TagsWiped,
            ct => _eventBus.PublishAsync(new SettingsTagsWipedSignalEvent(command.PrincipalId), ct),
            cancellationToken);
}
