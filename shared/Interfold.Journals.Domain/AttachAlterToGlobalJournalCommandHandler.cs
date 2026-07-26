using Interfold.Journals.Contracts;
using Interfold.Journals.Contracts.Models.Commands;
using Interfold.Journals.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Journals.Domain;

public sealed class AttachAlterToGlobalJournalCommandHandler : IdempotentCommandHandler<AttachAlterToGlobalJournalCommand, GlobalJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IAlterExistenceCheck _alterExistence;
    private readonly IClusterEventBus _eventBus;

    public AttachAlterToGlobalJournalCommandHandler(
        IJournalRepository journalRepository,
        IAlterExistenceCheck alterExistence,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _journalRepository = journalRepository;
        _alterExistence = alterExistence;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalGlobalAttachAlter;

protected override async Task<CommandExecutionResult<GlobalJournalCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<AttachAlterToGlobalJournalCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (await AlterJournalCommandFlow.RejectIfInvalidOrMissingAlterAsync<AttachAlterToGlobalJournalCommand, GlobalJournalCommandResult>(
                command,
                command.Payload.AlterId,
                _alterExistence,
                EntityRefs.AlterId,
                EntityRefs.JournalAlterNotFound,
                cancellationToken) is { } alterReject)
            return alterReject;

        return await GlobalJournalCommandFlow.ExecuteUpdateAsync(
            command,
            command.Payload.EntryId,
            _journalRepository,
            _eventBus,
            ct => _journalRepository.AttachGlobalAlterAsync(command.PrincipalId, command.Payload.EntryId, command.Payload.AlterId, ct),
            EntityRefs.JournalAttachFailed,
            cancellationToken);
    }

}
