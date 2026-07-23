using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Journals;

public sealed class SetGlobalJournalLockedCommandHandler : IdempotentCommandHandler<SetGlobalJournalLockedCommand, GlobalJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IClusterEventBus _eventBus;

    public SetGlobalJournalLockedCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _journalRepository = journalRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalGlobalSetLocked;

protected override async Task<CommandExecutionResult<GlobalJournalCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<SetGlobalJournalLockedCommand> command,
        CancellationToken cancellationToken = default)
    {
        return await GlobalJournalCommandFlow.ExecuteUpdateAsync(
            command,
            command.Payload.EntryId,
            _journalRepository,
            _eventBus,
            ct => _journalRepository.SetGlobalLockedAsync(command.PrincipalId, command.Payload.EntryId, command.Payload.Locked, ct),
            EntityRefs.JournalUpdateFailed,
            cancellationToken);
    }

}
