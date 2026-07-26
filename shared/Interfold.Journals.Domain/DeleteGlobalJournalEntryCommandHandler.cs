using Interfold.Journals.Contracts;
using Interfold.Journals.Contracts.Models.Commands;
using Interfold.Journals.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Journals.Domain;

public sealed class DeleteGlobalJournalEntryCommandHandler : IdempotentCommandHandler<DeleteGlobalJournalEntryCommand, GlobalJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IClusterEventBus _eventBus;

    public DeleteGlobalJournalEntryCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _journalRepository = journalRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalGlobalDelete;

protected override async Task<CommandExecutionResult<GlobalJournalCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<DeleteGlobalJournalEntryCommand> command,
        CancellationToken cancellationToken = default)
    {
        return await GlobalJournalCommandFlow.ExecuteDeleteAsync(
            command,
            command.Payload.EntryId,
            _journalRepository,
            _eventBus,
            ct => _journalRepository.DeleteGlobalAsync(command.PrincipalId, command.Payload.EntryId, ct),
            EntityRefs.JournalDeleteFailed,
            cancellationToken);
    }

}
