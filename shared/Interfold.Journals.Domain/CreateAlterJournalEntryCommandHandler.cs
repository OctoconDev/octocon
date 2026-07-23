using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Journals;

public sealed class CreateAlterJournalEntryCommandHandler : IdempotentCommandHandler<CreateAlterJournalEntryCommand, AlterJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IAlterExistenceCheck _alterExistence;
    private readonly IClusterEventBus _eventBus;

    public CreateAlterJournalEntryCommandHandler(
        IJournalRepository journalRepository,
        IAlterExistenceCheck alterExistence,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _journalRepository = journalRepository;
        _alterExistence = alterExistence;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalAlterCreate;

protected override async Task<CommandExecutionResult<AlterJournalCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<CreateAlterJournalEntryCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (RejectIfBlank(command, command.Payload.Title, EntityRefs.JournalTitleRequired) is { } blankReject)
            return blankReject;

        if (command.Payload.Title.Length > 100)
            return RejectInvariant(command, EntityRefs.JournalTitleTooLong);

        if (await AlterJournalCommandFlow.RejectIfInvalidOrMissingAlterAsync<CreateAlterJournalEntryCommand, AlterJournalCommandResult>(
                command,
                command.Payload.AlterId,
                _alterExistence,
                EntityRefs.AlterId,
                EntityRefs.JournalAlterNotFound,
                cancellationToken) is { } alterReject)
            return alterReject;

        return await AlterJournalCommandFlow.ExecuteCreateAsync(
            command,
            _eventBus,
            ct => _journalRepository.CreateAlterAsync(command.PrincipalId, command.Payload, ct),
            command.Payload.AlterId,
            EntityRefs.JournalCreateFailed,
            cancellationToken);
    }

}
