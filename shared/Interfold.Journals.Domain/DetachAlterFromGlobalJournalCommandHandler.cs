using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Journals;

public sealed class DetachAlterFromGlobalJournalCommandHandler : IdempotentCommandHandler<DetachAlterFromGlobalJournalCommand, GlobalJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IAlterExistenceCheck _alterExistence;
    private readonly IClusterEventBus _eventBus;

    public DetachAlterFromGlobalJournalCommandHandler(
        IJournalRepository journalRepository,
        IAlterExistenceCheck alterExistence,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _journalRepository = journalRepository;
        _alterExistence = alterExistence;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalGlobalDetachAlter;

protected override async Task<CommandExecutionResult<GlobalJournalCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<DetachAlterFromGlobalJournalCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (await AlterJournalCommandFlow.RejectIfInvalidOrMissingAlterAsync<DetachAlterFromGlobalJournalCommand, GlobalJournalCommandResult>(
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
            ct => _journalRepository.DetachGlobalAlterAsync(command.PrincipalId, command.Payload.EntryId, command.Payload.AlterId, ct),
            EntityRefs.JournalDetachFailed,
            cancellationToken);
    }

}
