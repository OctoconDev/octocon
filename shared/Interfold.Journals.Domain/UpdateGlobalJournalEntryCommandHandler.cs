using Interfold.Journals.Contracts;
using Interfold.Journals.Contracts.Models.Commands;
using Interfold.Journals.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Journals.Domain;

public sealed class UpdateGlobalJournalEntryCommandHandler : IdempotentCommandHandler<UpdateGlobalJournalEntryCommand, GlobalJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IClusterEventBus _eventBus;

    public UpdateGlobalJournalEntryCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _journalRepository = journalRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalGlobalUpdate;

protected override async Task<CommandExecutionResult<GlobalJournalCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<UpdateGlobalJournalEntryCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.Title is null && command.Payload.Content is null && command.Payload.Color is null)
            return RejectInvariant(command, EntityRefs.JournalNoFields);

        if (command.Payload.Title is not null && command.Payload.Title.Length > 250)
            return RejectInvariant(command, EntityRefs.JournalTitleTooLong);

        return await GlobalJournalCommandFlow.ExecuteUpdateAsync(
            command,
            command.Payload.EntryId,
            _journalRepository,
            _eventBus,
            ct => _journalRepository.UpdateGlobalAsync(command.PrincipalId, command.Payload, ct),
            EntityRefs.JournalUpdateFailed,
            cancellationToken);
    }

}
