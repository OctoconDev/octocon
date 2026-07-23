using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Journals;

public sealed class SetAlterJournalLockedCommandHandler : IdempotentCommandHandler<SetAlterJournalLockedCommand, AlterJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IClusterEventBus _eventBus;

    public SetAlterJournalLockedCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
        : base(idempotencyStore)
    {
        _journalRepository = journalRepository;
        _eventBus = eventBus;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalAlterSetLocked;


    protected override async Task<CommandExecutionResult<AlterJournalCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<SetAlterJournalLockedCommand> command,
        CancellationToken cancellationToken = default)
    {
        return await AlterJournalCommandFlow.ExecuteExistingEntryMutationAsync(
            command,
            command.Payload.EntryId,
            _journalRepository,
            ct => _journalRepository.SetAlterLockedAsync(command.PrincipalId, command.Payload.EntryId, command.Payload.Locked, ct),
            EntityRefs.JournalUpdateFailed,
            ct => _eventBus.PublishAsync(new AlterJournalEntryUpdatedEvent(command.PrincipalId, command.Payload.EntryId), ct),
            cancellationToken);
    }
}

public sealed class SetAlterJournalPinnedCommandHandler : IdempotentCommandHandler<SetAlterJournalPinnedCommand, AlterJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IClusterEventBus _eventBus;

    public SetAlterJournalPinnedCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
        : base(idempotencyStore)
    {
        _journalRepository = journalRepository;
        _eventBus = eventBus;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalAlterSetPinned;


    protected override async Task<CommandExecutionResult<AlterJournalCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<SetAlterJournalPinnedCommand> command,
        CancellationToken cancellationToken = default)
    {
        return await AlterJournalCommandFlow.ExecuteExistingEntryMutationAsync(
            command,
            command.Payload.EntryId,
            _journalRepository,
            ct => _journalRepository.SetAlterPinnedAsync(command.PrincipalId, command.Payload.EntryId, command.Payload.Pinned, ct),
            EntityRefs.JournalUpdateFailed,
            ct => _eventBus.PublishAsync(new AlterJournalEntryUpdatedEvent(command.PrincipalId, command.Payload.EntryId), ct),
            cancellationToken);
    }
}
