using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;

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

    // QualifyFriendship + QualifyFriendRequest overloads moved to
    // host/Interfold.Friendships.Api/Helpers/FriendshipAvatarQualifier.cs during the Phase-3
    // Friendships slice — this shared file no longer binds the friendship read-model cluster
    // (which now lives in Interfold.Friendships.Contracts). The per-avatar QualifyAvatar
    // primitives above stay here.
}
