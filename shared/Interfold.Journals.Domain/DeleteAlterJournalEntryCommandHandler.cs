using Interfold.Journals.Contracts;
using Interfold.Journals.Contracts.Events;
using Interfold.Journals.Contracts.Models.Commands;
using Interfold.Journals.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Journals.Domain;

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
