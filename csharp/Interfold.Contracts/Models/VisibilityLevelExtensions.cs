using Interfold.Contracts.Enums;

namespace Interfold.Contracts.Models;

/// <summary>
/// Viewer-gating helper for <see cref="VisibilityLevel"/>. Persistence-code round-trip
/// is handled by casting <c>(short)value</c> outbound and calling the fallback-less
/// <c>code.FromCode&lt;VisibilityLevel&gt;()</c> inbound; unknown or null on-disk codes
/// throw <see cref="ArgumentOutOfRangeException"/> so corrupt rows surface loudly rather
/// than silently coercing to <c>Public</c> or <c>Private</c>.
/// </summary>
public static class VisibilityLevelExtensions
{
    /// <summary>
    /// The single visibility gate: whether a viewer with <paramref name="friendshipLevel"/>
    /// (null = not a friend) may see an entity at this level. Fail-closed for unknown levels.
    /// </summary>
    public static bool CanBeViewedBy(this VisibilityLevel visibilityLevel, FriendshipLevel? friendshipLevel)
        => visibilityLevel switch
        {
            VisibilityLevel.Public => true,
            VisibilityLevel.FriendsOnly => friendshipLevel is FriendshipLevel.Friend or FriendshipLevel.TrustedFriend,
            VisibilityLevel.TrustedOnly => friendshipLevel is FriendshipLevel.TrustedFriend,
            _ => false,
        };
}
