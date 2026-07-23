using Interfold.Shared.Contracts.Enums;

namespace Interfold.Shared.Contracts.Models;

// Viewer-gating helper for VisibilityLevel. Persistence round-trip uses (short)value
// out / FromCode<VisibilityLevel>() in; corrupt codes throw so bad rows fail loudly.
public static class VisibilityLevelExtensions
{
    /// <summary>Fail-closed viewer gate. Null friendship = not a friend.</summary>
    public static bool CanBeViewedBy(this VisibilityLevel visibilityLevel, FriendshipLevel? friendshipLevel)
        => visibilityLevel switch
        {
            VisibilityLevel.Public => true,
            VisibilityLevel.FriendsOnly => friendshipLevel is FriendshipLevel.Friend or FriendshipLevel.TrustedFriend,
            VisibilityLevel.TrustedOnly => friendshipLevel is FriendshipLevel.TrustedFriend,
            _ => false,
        };
}
