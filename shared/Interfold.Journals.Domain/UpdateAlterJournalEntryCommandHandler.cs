using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Journals;

public sealed class UpdateAlterJournalEntryCommandHandler : IdempotentCommandHandler<UpdateAlterJournalEntryCommand, AlterJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IClusterEventBus _eventBus;

    public UpdateAlterJournalEntryCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _journalRepository = journalRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalAlterUpdate;

protected override async Task<CommandExecutionResult<AlterJournalCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<UpdateAlterJournalEntryCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.Title is null && command.Payload.Content is null && command.Payload.Color is null)
            return RejectInvariant(command, EntityRefs.JournalNoFields);

        if (command.Payload.Title is not null && command.Payload.Title.Length > 100)
            return RejectInvariant(command, EntityRefs.JournalTitleTooLong);

        if (command.Payload.Content is not null && command.Payload.Content.Length > 50_000)
            return RejectInvariant(command, EntityRefs.JournalContentTooLong);

        return await AlterJournalCommandFlow.ExecuteExistingEntryMutationAsync(
            command,
            command.Payload.EntryId,
            _journalRepository,
            ct => _journalRepository.UpdateAlterAsync(command.PrincipalId, command.Payload, ct),
            EntityRefs.JournalUpdateFailed,
            ct => _eventBus.PublishAsync(new AlterJournalEntryUpdatedEvent(command.PrincipalId, command.Payload.EntryId), ct),
            cancellationToken);
    }

}
