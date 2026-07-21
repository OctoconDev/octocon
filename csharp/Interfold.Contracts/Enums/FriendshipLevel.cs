using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Domain-facing friendship level used both on the wire (as JSON <c>"friend"</c> /
/// <c>"trusted_friend"</c>) and internally by the repositories for visibility gating.
///
/// <para>
/// The Scylla schema stores this as a <c>smallint</c>: <c>0 → friend</c>,
/// <c>1 → trusted_friend</c>. Callers cast <c>(short)value</c> outbound and use the
/// fallback-less <c>code.FromCode&lt;FriendshipLevel&gt;()</c> inbound; unknown or null
/// on-disk codes throw <see cref="ArgumentOutOfRangeException"/> so corrupt rows surface
/// at the read site rather than silently degrading to the lowest-tier level (the historic
/// "safe" fallback was <c>Friend</c>, but silently downgrading a trusted relationship is
/// a security-relevant behaviour that should be visible to operators, not hidden).
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
