using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Journals;

public sealed class DeleteAlterJournalEntryCommandHandler : IdempotentCommandHandler<DeleteAlterJournalEntryCommand, AlterJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IClusterEventBus _eventBus;

    public DeleteAlterJournalEntryCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _journalRepository = journalRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalAlterDelete;

protected override async Task<CommandExecutionResult<AlterJournalCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<DeleteAlterJournalEntryCommand> command,
        CancellationToken cancellationToken = default)
    {
        return await AlterJournalCommandFlow.ExecuteExistingEntryMutationAsync(
            command,
            command.Payload.EntryId,
            _journalRepository,
            ct => _journalRepository.DeleteAlterAsync(command.PrincipalId, command.Payload.EntryId, ct),
            EntityRefs.JournalDeleteFailed,
            ct => _eventBus.PublishAsync(new AlterJournalEntryDeletedEvent(command.PrincipalId, command.Payload.EntryId), ct),
            cancellationToken);
    }

}
