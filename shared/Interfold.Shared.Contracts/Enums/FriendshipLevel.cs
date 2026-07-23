using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Enums;

/// <summary>Friendship level. Wire: <c>"friend"</c> / <c>"trusted_friend"</c>. Storage:
/// smallint 0/1. Persistence read uses fallback-less <c>FromCode&lt;FriendshipLevel&gt;()</c>
/// so corrupt rows throw instead of silently downgrading a trusted relationship.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FriendshipLevel>))]
public enum FriendshipLevel : short
{
    [JsonStringEnumMemberName("friend")]
    Friend = 0,

    [JsonStringEnumMemberName("trusted_friend")]
    TrustedFriend = 1,
}
