using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Domain-facing friendship level used both on the wire (as JSON <c>"friend"</c> /
/// <c>"trusted_friend"</c>) and internally by the repositories for visibility gating.
///
/// <para>
/// The Scylla schema stores this as a <c>smallint</c>: <c>0 → friend</c>,
/// <c>1 → trusted_friend</c>. Callers cast <c>(short)value</c> outbound and use
/// <c>EnumCode&lt;FriendshipLevel&gt;.FromCode(code, FriendshipLevel.Friend)</c> inbound;
/// the <c>Friend</c> fallback is the historical fail-safe for corrupt rows (a bad value
/// must not silently grant trusted-tier visibility).
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FriendshipLevel>))]
public enum FriendshipLevel : short
{
    [JsonStringEnumMemberName("friend")]
    Friend = 0,

    [JsonStringEnumMemberName("trusted_friend")]
    TrustedFriend = 1,
}
