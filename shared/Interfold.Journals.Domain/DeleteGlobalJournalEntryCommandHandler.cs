using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Journals;

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
