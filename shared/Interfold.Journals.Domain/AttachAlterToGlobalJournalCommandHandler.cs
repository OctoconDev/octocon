using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Journals;

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
