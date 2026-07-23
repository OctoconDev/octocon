using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Alters;

public sealed class DeleteAlterCommandHandler : IdempotentCommandHandler<DeleteAlterCommand, AlterCommandResult>
{
    private readonly IAlterRepository _alterRepository;
    private readonly IJournalAlterCascade _journalCascade;
    private readonly IClusterEventBus _eventBus;

    public DeleteAlterCommandHandler(
        IAlterRepository alterRepository,
        IJournalAlterCascade journalCascade,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _alterRepository = alterRepository;
        _journalCascade = journalCascade;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.AlterDelete;

protected override async Task<CommandExecutionResult<AlterCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<DeleteAlterCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (AlterCommandFlow.RejectIfInvalidAlterId(command, command.Payload.AlterId, EntityRefs.AlterId) is { } rangeReject)
            return rangeReject;

        if (await AlterCommandFlow.RejectIfAlterNotFoundAsync(command, _alterRepository, command.Payload.AlterId, EntityRefs.AlterNotFound, cancellationToken) is { } notFoundReject)
            return notFoundReject;

        // Cascade BEFORE the alter row itself is removed so a journal-cleanup failure
        // leaves the alter intact (caller can retry); the inverse order would orphan the
        // journals if the alter delete succeeded but cleanup later threw. DeleteAllForAlterAsync
        // also detaches the alter from any global journals it was attached to without
        // deleting the global journal itself (multiple alters can share a group journal).
        await _journalCascade.DeleteAllForAlterAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);

        //TODO: Delete alter image if it exists

        return await AlterCommandFlow.ExecuteMutationAsync(
            command,
            command.Payload.AlterId,
            ct => _alterRepository.DeleteAsync(command.PrincipalId, command.Payload.AlterId, ct),
            EntityRefs.AlterDeleteFailed,
            ct => _eventBus.PublishAsync(new AlterDeletedEvent(command.PrincipalId, command.Payload.AlterId), ct),
            cancellationToken);
    }

}
