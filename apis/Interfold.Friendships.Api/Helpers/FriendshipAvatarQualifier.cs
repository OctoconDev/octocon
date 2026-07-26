using Interfold.Friendships.Contracts.Models.Read;
using Interfold.Shared.Api.Helpers;

namespace Interfold.Friendships.Api.Helpers;

// Feature-owned migration of the four QualifyFriendship / QualifyFriendRequest helpers
// that used to live in Interfold.Shared.Api/Helpers/AvatarUrlQualifier. Moved here as
// part of the Phase-3 Friendships slice so Interfold.Shared.Api no longer binds the
// friendship read-model cluster (which now lives in Interfold.Friendships.Contracts).
// The underlying per-avatar overloads (AvatarUrlQualifier.QualifyAvatar) stay in the
// shared spine — they're the shared primitive; the friendship-shaped composers are
// what belong to this feature.
internal static class FriendshipAvatarQualifier
{
    /// <summary>Qualifies the friend's and every fronting alter's avatar; other fields pass through.</summary>
    internal static FriendshipReadModel QualifyFriendship(
        FriendshipReadModel friendship,
        string scheme,
        HostString host)
        => friendship with
        {
            Friend = friendship.Friend with
            {
                AvatarUrl = AvatarUrlQualifier.QualifyAvatar(friendship.Friend, scheme, host),
            },
            Fronting = friendship.Fronting
                .Select(f => f with
                {
                    Alter = f.Alter with
                    {
                        AvatarUrl = AvatarUrlQualifier.QualifyAvatar(f.Alter, scheme, host),
                    },
                })
                .ToArray(),
        };

    /// <summary>Origin-string overload for socket pushers.</summary>
    internal static FriendshipReadModel QualifyFriendship(
        FriendshipReadModel friendship,
        string? origin)
        => friendship with
        {
            Friend = friendship.Friend with
            {
                AvatarUrl = AvatarUrlQualifier.QualifyAvatar(friendship.Friend, origin),
            },
            Fronting = friendship.Fronting
                .Select(f => f with
                {
                    Alter = f.Alter with
                    {
                        AvatarUrl = AvatarUrlQualifier.QualifyAvatar(f.Alter, origin),
                    },
                })
                .ToArray(),
        };

    /// <summary>Qualifies the requester/requestee profile's avatar.</summary>
    internal static FriendRequestReadModel QualifyFriendRequest(
        FriendRequestReadModel request,
        string scheme,
        HostString host)
        => request with
        {
            System = request.System with
            {
                AvatarUrl = AvatarUrlQualifier.QualifyAvatar(request.System, scheme, host),
            },
        };
}
