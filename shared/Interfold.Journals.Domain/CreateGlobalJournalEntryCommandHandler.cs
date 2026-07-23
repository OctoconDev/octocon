using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Journals;

public sealed class CreateGlobalJournalEntryCommandHandler : IdempotentCommandHandler<CreateGlobalJournalEntryCommand, GlobalJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IClusterEventBus _eventBus;

    public CreateGlobalJournalEntryCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _journalRepository = journalRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalGlobalCreate;

protected override async Task<CommandExecutionResult<GlobalJournalCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<CreateGlobalJournalEntryCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (RejectIfBlank(command, command.Payload.Title, EntityRefs.JournalTitleRequired) is { } blankReject)
            return blankReject;

        if (command.Payload.Title.Length > 250)
            return RejectInvariant(command, EntityRefs.JournalTitleTooLong);

        return await GlobalJournalCommandFlow.ExecuteCreateAsync(
            command,
            _journalRepository,
            _eventBus,
            ct => _journalRepository.CreateGlobalAsync(command.PrincipalId, command.Payload, ct),
            EntityRefs.JournalCreateFailed,
            cancellationToken);
    }

}
