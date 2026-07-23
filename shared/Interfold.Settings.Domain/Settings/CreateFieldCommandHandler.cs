using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Settings;

public sealed class CreateFieldCommandHandler : IdempotentCommandHandler<CreateFieldCommand, SettingsFieldCommandResult>
{
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IClusterEventBus _eventBus;

    public CreateFieldCommandHandler(ISettingsFieldRepository fieldRepository, IIdempotencyStore idempotencyStore, IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _fieldRepository = fieldRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsFieldCreate;

protected override async Task<CommandExecutionResult<SettingsFieldCommandResult>> ExecuteCoreAsync (CommandEnvelope<CreateFieldCommand> command, CancellationToken cancellationToken = default)
    {
        if (RejectIfBlank(command, command.Payload.Name, EntityRefs.SettingsFieldNameRequired) is { } blankReject)
            return blankReject;

        // Stamp the row with the envelope's OccurredAt rather than honouring whatever
        // InsertedAtUtc the caller put on the payload. The caller (SettingsController) sends
        // `default(DateTime)` precisely so the idempotency hash stays stable across retries
        // - see SettingsController.CreateField. The SP import bypasses this handler entirely
        // and sets InsertedAtUtc itself from the decoded ObjectId, so it isn't affected here.
        // OccurredAt is nullable on the envelope; fall back to UtcNow if missing.
        var insertedAtUtc = (command.OccurredAt ?? DateTimeOffset.UtcNow).UtcDateTime;

        return await SettingsFieldCommandFlow.ExecuteCreateAsync(
            command,
            SettingsFieldAction.FieldCreated,
            EntityRefs.SettingsFieldCreateFailed,
            ct => _fieldRepository.CreateAsync(
                command.PrincipalId,
                command.Payload.Name,
                command.Payload.Type,
                command.Payload.SecurityLevel,
                command.Payload.Locked,
                insertedAtUtc,
                ct),
            ct => _eventBus.PublishAsync(new SettingsFieldsChangedEvent(command.PrincipalId), ct),
            cancellationToken);
    }
}