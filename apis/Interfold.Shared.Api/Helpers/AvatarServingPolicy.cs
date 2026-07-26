namespace Interfold.Shared.Api.Helpers;

/// <summary>Decides whether the API process should serve avatar files and, if so, from
/// which physical root under which request path. Collapses <c>AvatarStorageRoot</c> ×
/// <c>AvatarPublicBase</c>:
/// <list type="bullet">
///   <item><description><c>AvatarStorageRoot</c> blank or missing → don't serve.</description></item>
///   <item><description><c>AvatarPublicBase</c> is an absolute non-loopback http(s) URL →
///     CDN/edge fronts the bytes → don't serve.</description></item>
///   <item><description>Otherwise the API is the origin; <c>RequestPath</c> comes from
///     <c>AvatarPublicBase</c> (or <c>defaultPublicBase</c> if blank).</description></item>
/// </list></summary>
public static class AvatarServingPolicy
{
    /// <summary>Default request path when <c>avatarPublicBase</c> is blank; matches the
    /// path <see cref="Services.LocalAvatarStorage"/> stamps into <c>avatar_url</c>.</summary>
    public const string DefaultPublicBase = "/avatars";

    /// <summary>Resolves the (serve?, physical root, request path) tuple. Pure aside from
    /// a single <see cref="Directory.Exists(string)"/> probe.</summary>
    public static (bool ShouldServe, string PhysicalRoot, string RequestPath) Resolve(
        string? avatarStorageRoot,
        string? avatarPublicBase,
        string defaultPublicBase = DefaultPublicBase)
    {
        if (string.IsNullOrWhiteSpace(avatarStorageRoot))
        {
            return (false, string.Empty, string.Empty);
        }

        if (!Directory.Exists(avatarStorageRoot))
        {
            return (false, string.Empty, string.Empty);
        }

        // Loopback carve-out: integration tests and same-origin dev stamp http://localhost/…
        // URLs that the API itself must still serve.
        if (!string.IsNullOrWhiteSpace(avatarPublicBase)
            && Uri.TryCreate(avatarPublicBase, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            if (IsLocalLoopbackHost(absolute.Host))
            {
                var localPath = absolute.AbsolutePath.TrimEnd('/');
                if (localPath.Length == 0)
                    localPath = defaultPublicBase.TrimEnd('/');

                return (true, avatarStorageRoot, localPath);
            }

            return (false, string.Empty, string.Empty);
        }

        var requestPath = string.IsNullOrWhiteSpace(avatarPublicBase)
            ? defaultPublicBase
            : avatarPublicBase!;

        if (!requestPath.StartsWith('/'))
        {
            requestPath = "/" + requestPath;
        }

        requestPath = requestPath.TrimEnd('/');

        // "/" collapses to "" which StaticFileOptions treats as "match every request".
        // Re-anchor to the default rather than let that escape.
        if (requestPath.Length == 0)
        {
            requestPath = defaultPublicBase.TrimEnd('/');
        }

        return (true, avatarStorageRoot, requestPath);
    }

    private static bool IsLocalLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || host.Equals("127.0.0.1", StringComparison.Ordinal)
               || host.Equals("[::1]", StringComparison.Ordinal)
               || host.Equals("::1", StringComparison.Ordinal);
    }
}
