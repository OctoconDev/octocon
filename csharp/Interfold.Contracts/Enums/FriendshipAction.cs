using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Outcome verb carried on <c>FriendshipCommandResult.Action</c>. Serialized into persisted
/// idempotency outcome payloads and HTTP response bodies, so the snake-case-lower wire
/// spellings ("accepted", "sent", …) are frozen.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FriendshipAction>))]
public enum FriendshipAction
{
    [JsonStringEnumMemberName("accepted")]
    Accepted,

    [JsonStringEnumMemberName("sent")]
    Sent,

    [JsonStringEnumMemberName("removed")]
    Removed,

    [JsonStringEnumMemberName("trusted")]
    Trusted,

    [JsonStringEnumMemberName("untrusted")]
    Untrusted,

    [JsonStringEnumMemberName("rejected")]
    Rejected,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
}
