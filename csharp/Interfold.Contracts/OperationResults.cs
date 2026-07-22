using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.ImportOperations;

namespace Interfold.Contracts;

public sealed record AccountCommandResult(SystemId SystemId, Username Username, bool Replay) : ICommandResult<AccountCommandResult>
{
    public AccountCommandResult WithReplay() => this with { Replay = true };
}

public sealed record AlterCommandResult(SystemId SystemId, AlterId AlterId, bool Replay) : ICommandResult<AlterCommandResult>
{
    public AlterCommandResult WithReplay() => this with { Replay = true };
}

public sealed record FrontCommandResult(SystemId SystemId, AlterId? AlterId, FrontId? FrontId, bool Replay) : ICommandResult<FrontCommandResult>
{
    public FrontCommandResult WithReplay() => this with { Replay = true };
}

public sealed record TagCommandResult(SystemId SystemId, TagId TagId, bool Replay) : ICommandResult<TagCommandResult>
{
    public TagCommandResult WithReplay() => this with { Replay = true };
}

public sealed record PollCommandResult(SystemId SystemId, PollId PollId, bool Replay) : ICommandResult<PollCommandResult>
{
    public PollCommandResult WithReplay() => this with { Replay = true };
}

public sealed record GlobalJournalCommandResult(SystemId SystemId, EntryId EntryId, bool Replay) : ICommandResult<GlobalJournalCommandResult>
{
    public GlobalJournalCommandResult WithReplay() => this with { Replay = true };
}

public sealed record AlterJournalCommandResult(SystemId SystemId, EntryId EntryId, AlterId AlterId, bool Replay) : ICommandResult<AlterJournalCommandResult>
{
    public AlterJournalCommandResult WithReplay() => this with { Replay = true };
}

public sealed record FriendshipCommandResult(SystemId SystemId, SystemId TargetSystemId, FriendshipAction Action, bool Replay) : ICommandResult<FriendshipCommandResult>
{
    public FriendshipCommandResult WithReplay() => this with { Replay = true };
}

public sealed record SettingsCommandResult(SystemId SystemId, SettingsAction Action, bool Replay) : ICommandResult<SettingsCommandResult>
{
    public SettingsCommandResult WithReplay() => this with { Replay = true };
}

/// <summary>Result of dispatching an async SP/PK import onto the in-process worker queue;
/// serialised as the HTTP 202 body. Status: <c>"queued"</c> = fresh slot claimed;
/// <c>"running"</c> = collapsed onto an existing operation.</summary>
public sealed record ImportDispatchCommandResult(
    SystemId SystemId,
    ImportOperationId OperationId,
    ImportOperationKind Kind,
    ImportOperationDispatchStatus Status,
    DateTimeOffset StartedAt,
    bool Replay) : ICommandResult<ImportDispatchCommandResult>
{
    public ImportDispatchCommandResult WithReplay() => this with { Replay = true };
}

public sealed record SettingsFieldCommandResult(SystemId SystemId, SettingsFieldAction Action, FieldId FieldId, bool Replay) : ICommandResult<SettingsFieldCommandResult>
{
    public SettingsFieldCommandResult WithReplay() => this with { Replay = true };
}

public sealed record EncryptionCommandResult(SystemId SystemId, EncryptionAction Action, EncryptionKeyMaterial Key, bool Replay) : ICommandResult<EncryptionCommandResult>
{
    public EncryptionCommandResult WithReplay() => this with { Replay = true };
}
