using Interfold.Journals.Contracts;
using Interfold.Journals.Contracts.Models.Commands;
using Interfold.Journals.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Journals.Domain;

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
