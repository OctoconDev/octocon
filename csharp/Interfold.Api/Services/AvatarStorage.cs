using System.Text.RegularExpressions;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Ids;
using Microsoft.Extensions.Options;

namespace Interfold.Api.Services;

public interface IAvatarStorage
{
    /// <summary>
    /// Persist a system-level avatar and return the public URL as an <see cref="AvatarUrl"/>.
    /// </summary>
    Task<AvatarUrl> SaveSystemAvatarAsync(SystemId systemId, Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist an alter-level avatar and return the public URL as an <see cref="AvatarUrl"/>.
    /// </summary>
    Task<AvatarUrl> SaveAlterAvatarAsync(SystemId systemId, AlterId alterId, Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete the file backing <paramref name="avatarUrl"/> when it is a local URL owned by
    /// this storage. Accepts <see cref="AvatarUrl"/>? so callers stop unwrapping
    /// <c>currentAvatarUrl?.Value</c> at the boundary — the wrapper preserves nullability
    /// through <see cref="AvatarUrl.FromNullable"/>.
    /// </summary>
    Task<bool> DeleteByUrlAsync(AvatarUrl? avatarUrl, CancellationToken cancellationToken = default);
}

public sealed class LocalAvatarStorage : IAvatarStorage
{
    private static readonly Regex SafeSegmentPattern = new("[^a-zA-Z0-9_-]", RegexOptions.Compiled);

    private readonly IOptionsMonitor<StorageConfiguration> _storageOptions;
    private readonly string _webRootFallback;
    private readonly string _publicBaseFallback;

    // Read CurrentValue per-access so appsettings.json changes take effect without restart.
    private string StorageRoot => _storageOptions.CurrentValue.AvatarStorageRoot ?? _webRootFallback;
    private string PublicBase  => _storageOptions.CurrentValue.AvatarPublicBase  ?? _publicBaseFallback;

    public LocalAvatarStorage(IWebHostEnvironment environment, IOptionsMonitor<StorageConfiguration> storageOptions)
    {
        var webRoot = environment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            webRoot = Path.Combine(environment.ContentRootPath, "wwwroot");
        }

        _storageOptions     = storageOptions;
        _webRootFallback    = Path.Combine(webRoot, "avatars");
        _publicBaseFallback = "/avatars";
    }

    public Task<AvatarUrl> SaveSystemAvatarAsync(SystemId systemId, Stream stream, CancellationToken cancellationToken = default)
        => SaveAsync(systemId, "self", stream, cancellationToken);

    public Task<AvatarUrl> SaveAlterAvatarAsync(SystemId systemId, AlterId alterId, Stream stream, CancellationToken cancellationToken = default)
        => SaveAsync(systemId, alterId.ToString(), stream, cancellationToken);

    public Task<bool> DeleteByUrlAsync(AvatarUrl? avatarUrl, CancellationToken cancellationToken = default)
    {
        // AvatarUrl exposes .Value at the driver / filesystem boundary; the wrapper is a
        // no-op at runtime but pins the primitive-obsession contract at compile time.
        if (avatarUrl is not { } url || string.IsNullOrWhiteSpace(url.Value))
            return Task.FromResult(false);

        var basePath = GetPublicBasePath(PublicBase);
        var storageRoot = Path.GetFullPath(StorageRoot);
        var storageRootWithSep = storageRoot.EndsWith(Path.DirectorySeparatorChar)
            ? storageRoot
            : storageRoot + Path.DirectorySeparatorChar;

        var urlPath = url.Value;
        if (Uri.TryCreate(urlPath, UriKind.Absolute, out var absoluteUri))
            urlPath = absoluteUri.AbsolutePath;

        if (string.IsNullOrWhiteSpace(urlPath))
            return Task.FromResult(false);

        if (!urlPath.StartsWith('/'))
            urlPath = "/" + urlPath;

        if (!urlPath.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);

        var relativePath = urlPath[basePath.Length..].TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var fullFilePath = Path.GetFullPath(Path.Combine(storageRoot, relativePath));

        // Refuse to delete anything outside avatar storage root.
        if (!fullFilePath.StartsWith(storageRootWithSep, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);

        if (!File.Exists(fullFilePath))
            return Task.FromResult(false);

        File.Delete(fullFilePath);
        TryDeleteEmptyParentDirectories(storageRoot, Path.GetDirectoryName(fullFilePath));
        return Task.FromResult(true);
    }

    // targetId legitimately polymorphs between "self" (system avatar) and alterId.ToString()
    // (alter avatar), so it stays a string segment — no wrapper covers both shapes. systemId
    // is typed at the boundary so callers no longer pre-unwrap .Value only for this method
    // to re-normalize immediately. Return type is AvatarUrl so the wrap happens in exactly
    // one place instead of once per call site.
    private async Task<AvatarUrl> SaveAsync(SystemId systemId, string targetId, Stream stream, CancellationToken cancellationToken)
    {
        var rawSystemId = ScopedSystemId.StripRegionPrefix(systemId);
        var safeSystemId = SafeSegmentPattern.Replace(rawSystemId, "_");
        var safeTargetId = SafeSegmentPattern.Replace(targetId, "_");

        if (stream.CanSeek)
            stream.Position = 0;

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var content = buffer.ToArray();
        if (content.Length == 0)
            throw new InvalidOperationException("Uploaded file has no readable content.");

        var extension = DetectExtension(content);
        var fileName = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}{extension}";

        var directoryPath = Path.Combine(StorageRoot, safeSystemId, safeTargetId);
        Directory.CreateDirectory(directoryPath);

        var filePath = Path.Combine(directoryPath, fileName);
        await File.WriteAllBytesAsync(filePath, content, cancellationToken);

        var basePath = PublicBase.TrimEnd('/');
        return new($"{basePath}/{safeSystemId}/{safeTargetId}/{fileName}");
    }

    /// <summary>
    /// Normalises <see cref="StorageConfiguration.AvatarPublicBase"/> to the URL-path prefix
    /// used for filesystem layout and delete matching. Absolute http(s) values keep their
    /// full URL for stamping into <c>avatar_url</c> but expose <see cref="Uri.AbsolutePath"/>
    /// here so path comparisons line up with request paths.
    /// </summary>
    private static string GetPublicBasePath(string publicBase)
    {
        if (Uri.TryCreate(publicBase, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.AbsolutePath.TrimEnd('/');
        }

        var path = publicBase.TrimEnd('/');
        if (path.Length > 0 && !path.StartsWith('/'))
            path = "/" + path;

        return path;
    }

    private static string DetectExtension(byte[] data) => data switch
    {
        [0xFF, 0xD8, ..] => ".jpg",
        [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
        [0x47, 0x49, 0x46, ..] => ".gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => ".webp",
        _ => ".bin"
    };

    private static void TryDeleteEmptyParentDirectories(string storageRoot, string? directoryPath)
    {
        var rootFull = Path.GetFullPath(storageRoot).TrimEnd(Path.DirectorySeparatorChar);
        var current = directoryPath;

        while (!string.IsNullOrWhiteSpace(current))
        {
            var currentFull = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(currentFull, rootFull, StringComparison.OrdinalIgnoreCase))
                return;

            if (!Directory.Exists(currentFull))
                return;

            if (Directory.EnumerateFileSystemEntries(currentFull).Any())
                return;

            Directory.Delete(currentFull);
            current = Path.GetDirectoryName(currentFull);
        }
    }
}
