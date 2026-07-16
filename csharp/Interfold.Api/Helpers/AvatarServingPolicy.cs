namespace Interfold.Api.Helpers;

/// <summary>
/// Decides whether the API process itself should serve avatar files (and, if so, from
/// which filesystem location and under which request path). Centralises the policy so
/// the Program.cs static-file wiring and any future test/diagnostic surfaces agree on
/// the same matrix of <c>AvatarStorageRoot</c> × <c>AvatarPublicBase</c>.
/// </summary>
/// <remarks>
/// The configuration matrix the helper collapses:
/// <list type="bullet">
///   <item>
///     <description>
///       <c>AvatarStorageRoot</c> blank or missing on disk → don't serve. The API has no
///       directory to hand files out of; the operator either disabled local storage
///       entirely (everything is external) or hasn't mounted the volume yet.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>AvatarPublicBase</c> set to an absolute <c>http</c>/<c>https</c> URL → don't
///       serve. An absolute base means a CDN / reverse proxy fronts the bytes; the API
///       only writes them and stamps the CDN origin in <c>avatar_url</c>. Mounting a
///       second <see cref="Microsoft.AspNetCore.StaticFiles.StaticFileMiddleware"/>
///       under those conditions would duplicate the serving surface and confuse cache
///       invalidation.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>AvatarStorageRoot</c> set and on disk, <c>AvatarPublicBase</c> blank or
///       path-only → serve. The API is the canonical origin for the bytes; the
///       <c>RequestPath</c> derives from the <c>AvatarPublicBase</c> (or the
///       <c>defaultPublicBase</c> fallback when blank) so the URLs returned by
///       <see cref="AvatarUrlQualifier.QualifyAvatar(Interfold.Contracts.Ids.AvatarUrl?, Interfold.Contracts.Enums.AvatarSource?, string, Microsoft.AspNetCore.Http.HostString)"/>
///       line up exactly with what
///       <see cref="Microsoft.AspNetCore.Builder.StaticFileExtensions.UseStaticFiles(Microsoft.AspNetCore.Builder.IApplicationBuilder, Microsoft.AspNetCore.Builder.StaticFileOptions)"/>
///       matches.
///     </description>
///   </item>
/// </list>
/// The helper is pure (no IO except a single <see cref="Directory.Exists(string)"/>
/// probe) so it's trivially unit-testable against a temp directory.
/// </remarks>
public static class AvatarServingPolicy
{
    /// <summary>
    /// Default request path used when <paramref name="avatarPublicBase"/> is blank.
    /// Matches the path segment <see cref="Services.LocalAvatarStorage"/> stamps into
    /// <c>avatar_url</c> for newly-stored files, so the static-file middleware and the
    /// URL qualifier agree on the public surface by default.
    /// </summary>
    public const string DefaultPublicBase = "/avatars";

    /// <summary>
    /// Returns whether the API should mount a static-file middleware for avatars and,
    /// when it should, the (physical root, request path) tuple to wire it up with.
    /// </summary>
    /// <param name="avatarStorageRoot">
    /// The on-disk directory housing avatar files. Typically read from
    /// <c>StorageConfiguration.AvatarStorageRoot</c>. Blank or non-existent disables
    /// serving entirely.
    /// </param>
    /// <param name="avatarPublicBase">
    /// The public-facing base for avatar URLs. Typically read from
    /// <c>StorageConfiguration.AvatarPublicBase</c>. An absolute <c>http</c>/<c>https</c>
    /// URL means a CDN/edge fronts the bytes and the API must NOT serve; a path-only
    /// value (or blank) means the API is the origin and that path is the
    /// <c>RequestPath</c>.
    /// </param>
    /// <param name="defaultPublicBase">
    /// Fallback used when <paramref name="avatarPublicBase"/> is blank. Defaults to
    /// <see cref="DefaultPublicBase"/>; the parameter exists so unit tests can drive
    /// the resolution explicitly.
    /// </param>
    /// <returns>
    /// <c>ShouldServe = false</c> means the caller MUST NOT register a static-file
    /// middleware for avatars. When <c>true</c>, <c>PhysicalRoot</c> is the absolute
    /// filesystem path to feed
    /// <see cref="Microsoft.Extensions.FileProviders.PhysicalFileProvider"/>, and
    /// <c>RequestPath</c> is the leading-slashed, trailing-slash-trimmed URL prefix
    /// to feed <see cref="Microsoft.AspNetCore.Builder.StaticFileOptions.RequestPath"/>.
    /// </returns>
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

        // Absolute http(s) base → CDN / reverse proxy in front unless the host is local
        // loopback (integration tests and same-origin dev setups stamp http://localhost/…
        // URLs that the API process itself must still serve).
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

        // After trimming the trailing slash a pathological input of "/" collapses to
        // the empty string, which StaticFileOptions.RequestPath would treat as "every
        // request goes through the static-file middleware". Re-anchor to the default
        // rather than letting that escape — operators that intentionally want the
        // root prefix can pick a non-degenerate base.
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
