using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.ImportOperations;

namespace Interfold.Contracts;

public sealed record AccountCommandResult(SystemId SystemId, Username Username, bool Replay) : ICommandResult;

public sealed record AlterCommandResult(SystemId SystemId, AlterId AlterId, bool Replay) : ICommandResult;

public sealed record FrontCommandResult(SystemId SystemId, AlterId? AlterId, FrontId? FrontId, bool Replay) : ICommandResult;

public sealed record TagCommandResult(SystemId SystemId, TagId TagId, bool Replay) : ICommandResult;

public sealed record PollCommandResult(SystemId SystemId, PollId PollId, bool Replay) : ICommandResult;

public sealed record GlobalJournalCommandResult(SystemId SystemId, EntryId EntryId, bool Replay) : ICommandResult;

public sealed record AlterJournalCommandResult(SystemId SystemId, EntryId EntryId, AlterId AlterId, bool Replay) : ICommandResult;

public sealed record FriendshipCommandResult(SystemId SystemId, SystemId TargetSystemId, FriendshipAction Action, bool Replay) : ICommandResult;

public sealed record SettingsCommandResult(SystemId SystemId, SettingsAction Action, bool Replay) : ICommandResult;

/// <summary>
/// Result of dispatching an asynchronous third-party import (SP or PK) onto the in-process
/// worker queue. Returned by <c>ImportSpCommandHandler</c> / <c>ImportPkCommandHandler</c>
/// and serialised by the controller as the HTTP 202 Accepted body.
///
/// <para>
/// The <see cref="Status"/> field distinguishes the two dispatch outcomes:
/// </para>
/// <list type="bullet">
///   <item><c>"queued"</c> — this dispatch claimed a fresh per-system slot; a worker will pick it up.</item>
///   <item><c>"running"</c> — an import was already in flight for the same system; this dispatch collapsed onto the existing operation and the caller can subscribe to the same WebSocket completion frame.</item>
/// </list>
/// </summary>
public sealed record ImportDispatchCommandResult(
    SystemId SystemId,
    ImportOperationId OperationId,
    ImportOperationKind Kind,
    ImportOperationDispatchStatus Status,
    DateTimeOffset StartedAt,
    bool Replay) : ICommandResult;

public sealed record SettingsFieldCommandResult(SystemId SystemId, SettingsFieldAction Action, FieldId FieldId, bool Replay) : ICommandResult;

public sealed record EncryptionCommandResult(SystemId SystemId, EncryptionAction Action, EncryptionKeyMaterial Key, bool Replay) : ICommandResult;