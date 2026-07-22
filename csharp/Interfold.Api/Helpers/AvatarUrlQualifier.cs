using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;

namespace Interfold.Api.Helpers;

internal static class AvatarUrlQualifier
{
    /// <summary>Prepends the server origin to relative <paramref name="url"/>; absolute
    /// URLs pass through. Non-avatar callers only — avatar callers use
    /// <see cref="QualifyAvatar(AvatarUrl?, AvatarSource?, string, HostString)"/> which
    /// consults <c>avatar_source</c>.</summary>
    internal static string? Qualify(string? url, string scheme, HostString host)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;

        var origin = $"{scheme}://{host}";
        return url.StartsWith('/') ? $"{origin}{url}" : $"{origin}/{url}";
    }

    /// <summary>Overload for callers that already hold a pre-built origin string.</summary>
    internal static string? Qualify(string? url, string? origin)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        if (string.IsNullOrWhiteSpace(origin))
            return url;

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;

        return url.StartsWith('/') ? $"{origin}{url}" : $"{origin}/{url}";
    }

    /// <summary>Source-aware: <see cref="AvatarSource.Local"/> gets the origin prepended,
    /// <see cref="AvatarSource.External"/> is returned verbatim, null/blank passes through.</summary>
    internal static AvatarUrl? QualifyAvatar(AvatarUrl? url, AvatarSource? source, string scheme, HostString host)
    {
        if (url is not { } present || string.IsNullOrWhiteSpace(present.Value))
            return url;

        return source == AvatarSource.Local
            ? AvatarUrl.FromNullable(Qualify(present.Value, scheme, host))
            : url;
    }

    /// <summary>Origin-string overload for socket handlers that hold a pre-built origin.</summary>
    internal static AvatarUrl? QualifyAvatar(AvatarUrl? url, AvatarSource? source, string? origin)
    {
        if (url is not { } present || string.IsNullOrWhiteSpace(present.Value))
            return url;

        return source == AvatarSource.Local
            ? AvatarUrl.FromNullable(Qualify(present.Value, origin))
            : url;
    }

    /// <summary><see cref="IAvatarBearing"/> overload; null bearings pass through so
    /// <c>profile?.QualifyAvatar(...)</c> stays a one-liner.</summary>
    internal static AvatarUrl? QualifyAvatar(IAvatarBearing? bearing, string scheme, HostString host)
        => bearing is null ? null : QualifyAvatar(bearing.AvatarUrl, bearing.AvatarSource, scheme, host);

    /// <summary>Origin-string overload of the <see cref="IAvatarBearing"/> qualifier.</summary>
    internal static AvatarUrl? QualifyAvatar(IAvatarBearing? bearing, string? origin)
        => bearing is null ? null : QualifyAvatar(bearing.AvatarUrl, bearing.AvatarSource, origin);

    /// <summary>Qualifies the friend's and every fronting alter's avatar; other fields pass through.</summary>
    internal static FriendshipReadModel QualifyFriendship(
        FriendshipReadModel friendship,
        string scheme,
        HostString host)
        => friendship with
        {
            Friend = friendship.Friend with
            {
                AvatarUrl = QualifyAvatar(friendship.Friend, scheme, host),
            },
            Fronting = friendship.Fronting
                .Select(f => f with
                {
                    Alter = f.Alter with
                    {
                        AvatarUrl = QualifyAvatar(f.Alter, scheme, host),
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
                AvatarUrl = QualifyAvatar(friendship.Friend, origin),
            },
            Fronting = friendship.Fronting
                .Select(f => f with
                {
                    Alter = f.Alter with
                    {
                        AvatarUrl = QualifyAvatar(f.Alter, origin),
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
                AvatarUrl = QualifyAvatar(request.System, scheme, host),
            },
        };
}
