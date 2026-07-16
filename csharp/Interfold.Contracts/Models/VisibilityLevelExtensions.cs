using Interfold.Contracts.Enums;

namespace Interfold.Contracts.Models;

/// <summary>
/// Viewer-gating helper for <see cref="VisibilityLevel"/>. Persistence-code round-trip
/// is handled by casting <c>(short)value</c> outbound and calling
/// <c>EnumCode&lt;VisibilityLevel&gt;.FromCode(code, VisibilityLevel.Public)</c> (or
/// <c>VisibilityLevel.Private</c> for the fail-closed settings-field path) inbound.
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
