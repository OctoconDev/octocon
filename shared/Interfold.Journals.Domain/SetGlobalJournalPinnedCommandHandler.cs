using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Domain.Journals;

public sealed class SetGlobalJournalPinnedCommandHandler : IdempotentCommandHandler<SetGlobalJournalPinnedCommand, GlobalJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IClusterEventBus _eventBus;

    public SetGlobalJournalPinnedCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _journalRepository = journalRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalGlobalSetPinned;

protected override async Task<CommandExecutionResult<GlobalJournalCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<SetGlobalJournalPinnedCommand> command,
        CancellationToken cancellationToken = default)
    {
        return await GlobalJournalCommandFlow.ExecuteUpdateAsync(
            command,
            command.Payload.EntryId,
            _journalRepository,
            _eventBus,
            ct => _journalRepository.SetGlobalPinnedAsync(command.PrincipalId, command.Payload.EntryId, command.Payload.Pinned, ct),
            EntityRefs.JournalUpdateFailed,
            cancellationToken);
    }

}
