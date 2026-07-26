using System.Text.Json;
using Interfold.Polls.Contracts.Ids;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Polls.Contracts.Models.Commands;

/// <summary>
/// Command payload for creating a poll. <see cref="InsertedAtUtc"/> is a server-side
/// timestamp supplied by the caller: the public API uses "now" (matching the envelope's
/// <c>OccurredAt</c>), while the Simply Plural import decodes SP's MongoDB ObjectId
/// (the first 4 bytes are a big-endian Unix-seconds creation timestamp). The importer
/// falls back to SP's <c>lastOperationTime</c>, then to import time, only when the SP
/// id isn't a 24-hex ObjectId - both fallbacks log a warning so operators can spot
/// historical polls whose creation date isn't trustworthy.
/// </summary>
public sealed record CreatePollCommand(
    string Title,
    string? Description,
    PollType Type,
    DateTime? TimeEnd,
    DateTime InsertedAtUtc
);

// The Id property name is frozen: it serializes as "id" in persisted command JSON
// and idempotency hashes.
public sealed record UpdatePollCommand(
    PollId Id,
    string? Title,
    string? Description,
    DateTime? TimeEnd,
    bool HasTimeEnd,
    JsonElement? Data
);

public sealed record DeletePollCommand(PollId PollId);
