using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;

namespace Interfold.Api.Helpers;

internal static class AvatarUrlQualifier
{
    /// <summary>
    /// Returns <paramref name="url"/> with the server origin prepended when the stored
    /// value is a relative path. Already-absolute URLs are returned unchanged.
    /// </summary>
    /// <remarks>
    /// Avatar paths route through <see cref="QualifyAvatar(AvatarUrl?, AvatarSource?, string, HostString)"/>
    /// (or the origin overload) so they can use the persisted <c>avatar_source</c> as the
    /// authoritative discriminator. This raw helper is retained for non-avatar callers
    /// (e.g. internal utilities) that don't have a source flag to inspect.
    /// </remarks>
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

    /// <summary>
    /// Overload for callers that already hold a pre-built origin string
    /// (e.g. <c>"https://api.example.com"</c>).
    /// </summary>
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

    /// <summary>
    /// Source-aware avatar qualification. The persisted <see cref="AvatarSource"/> is the
    /// single source of truth: <see cref="AvatarSource.Local"/> URLs get the server origin
    /// prepended, <see cref="AvatarSource.External"/> URLs are returned verbatim, and a
    /// null / blank input is passed through unchanged.
    /// </summary>
    internal static AvatarUrl? QualifyAvatar(AvatarUrl? url, AvatarSource? source, string scheme, HostString host)
    {
        if (url is not { } present || string.IsNullOrWhiteSpace(present.Value))
            return url;

        return source == AvatarSource.Local
            ? AvatarUrl.FromNullable(Qualify(present.Value, scheme, host))
            : url;
    }

    /// <summary>
    /// Origin-string overload of <see cref="QualifyAvatar(AvatarUrl?, AvatarSource?, string, HostString)"/>
    /// for socket handlers and other callers that already hold a pre-built origin string.
    /// </summary>
    internal static AvatarUrl? QualifyAvatar(AvatarUrl? url, AvatarSource? source, string? origin)
    {
        if (url is not { } present || string.IsNullOrWhiteSpace(present.Value))
            return url;

        return source == AvatarSource.Local
            ? AvatarUrl.FromNullable(Qualify(present.Value, origin))
            : url;
    }

    /// <summary>
    /// <see cref="IAvatarBearing"/> overload of
    /// <see cref="QualifyAvatar(AvatarUrl?, AvatarSource?, string, HostString)"/>. Every
    /// avatar-carrying read model implements the interface, so callers pass the bearing
    /// itself instead of the <c>x.AvatarUrl, x.AvatarSource</c> pair that was previously
    /// spelled at every callsite. Null bearings pass through as <c>null</c> so the
    /// <c>profile?.QualifyAvatar(...)</c> style at socket boundaries stays a one-liner.
    /// </summary>
    internal static AvatarUrl? QualifyAvatar(IAvatarBearing? bearing, string scheme, HostString host)
        => bearing is null ? null : QualifyAvatar(bearing.AvatarUrl, bearing.AvatarSource, scheme, host);

    /// <summary>
    /// Origin-string overload of the <see cref="IAvatarBearing"/> qualifier — pairs with
    /// <see cref="QualifyAvatar(AvatarUrl?, AvatarSource?, string?)"/> for socket callers
    /// that already hold a pre-built <c>RequestOrigin</c>.
    /// </summary>
    internal static AvatarUrl? QualifyAvatar(IAvatarBearing? bearing, string? origin)
        => bearing is null ? null : QualifyAvatar(bearing.AvatarUrl, bearing.AvatarSource, origin);

    /// <summary>
    /// Returns <paramref name="friendship"/> with the friend's avatar and every fronting
    /// alter's avatar qualified against the server origin. Preserves the input's field
    /// values verbatim except for the two <see cref="AvatarUrl"/> holes; external avatar
    /// sources pass through unchanged per the underlying
    /// <see cref="QualifyAvatar(AvatarUrl?, AvatarSource?, string, HostString)"/> rules.
    /// </summary>
    /// <remarks>
    /// Extracted so the identical 6-line "record with { Friend, Fronting.Select(...) }"
    /// shape stops being duplicated across FriendsController, PublicSystemsController.Batch,
    /// and the socket-side FriendshipSocketEventHandlers.
    /// </remarks>
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

    /// <summary>
    /// Origin-string overload for socket-side pushers that hold a pre-built
    /// <c>RequestOrigin</c> from the WebSocket handshake context.
    /// </summary>
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

    /// <summary>
    /// Returns <paramref name="request"/> with the requester/requestee profile's avatar
    /// qualified against the server origin. Same shape as
    /// <see cref="QualifyFriendship(FriendshipReadModel, string, HostString)"/> — kept
    /// separate so incoming/outgoing list projections don't need to spell out the
    /// <c>System = x.System with { AvatarUrl = ... }</c> lambda inline.
    /// </summary>
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
