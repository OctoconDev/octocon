using System.Text.Json;
using Interfold.Polls.Contracts.Ids;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Polls.Contracts.Models.Read;

public sealed record PollReadModel(
    PollId Id,
    SystemId UserId,
    string Title,
    string? Description,
    PollType Type,
    JsonElement Data,
    DateTime? TimeEnd,
    DateTime InsertedAt,
    DateTime UpdatedAt
);


public sealed record CreatePollRequest(
    string Title,
    string? Description = null,
    PollType? Type = null,
    DateTime? TimeEnd = null
);

/// <summary>
/// Sparse-diff PATCH body: the client only includes changed keys, and <c>time_end</c>
/// is tri-state (absent = unchanged, null = clear the deadline, string = set it) —
/// hence <see cref="PatchValue{T}"/> rather than a plain nullable.
/// </summary>
public sealed record UpdatePollRequest(
    string? Title = null,
    string? Description = null,
    PatchValue<DateTime> TimeEnd = default,
    JsonElement? Data = null
);

