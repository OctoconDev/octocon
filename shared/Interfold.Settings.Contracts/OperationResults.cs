using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.ImportOperations;

namespace Interfold.Contracts;

public sealed record AccountCommandResult(SystemId SystemId, Username Username, bool Replay) : ICommandResult<AccountCommandResult>
{
    public AccountCommandResult WithReplay() => this with { Replay = true };
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
