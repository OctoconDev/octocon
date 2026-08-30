using Interfold.Shared.Contracts.Enums;

namespace Interfold.Shared.Contracts.Models;

// Viewer-gating helper for VisibilityLevel. Persistence writes (short)value;
// reads go through FromStorage so a null/unknown code cannot abort owner list/join.
// Unknown maps to Private — fail closed for non-owners.
public static class VisibilityLevelExtensions
{
    /// <summary>Scylla rehydration. Null or unknown → <see cref="VisibilityLevel.Private"/>.</summary>
    public static VisibilityLevel FromStorage(this short? code)
        => code.FromCode(VisibilityLevel.Private);

    public static VisibilityLevel FromStorage(this short code)
        => FromStorage((short?)code);

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
