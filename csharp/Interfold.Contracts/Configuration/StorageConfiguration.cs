namespace Interfold.Contracts.Configuration;

using Interfold.Contracts.Configuration.Validation;

/// <summary>
/// Local file storage configuration for avatars and other static assets.
/// Binds from environment variables with OCTOCON_ prefix.
/// </summary>
public sealed class StorageConfiguration
{
    public const string SectionName = "Octocon:Storage";

    /// <summary>
    /// Local filesystem root directory for storing uploaded avatars.
    /// Env: OCTOCON_AVATAR_STORAGE_ROOT
    /// Must be an absolute path when set; a relative path is rejected at
    /// <c>ValidateOnStart</c> time by <see cref="AbsolutePathAttribute"/>.
    /// </summary>
    [AbsolutePath]
    public string? AvatarStorageRoot { get; set; }

    /// <summary>
    /// Public base URL for accessing stored avatars (e.g., 'https://cdn.example.com/avatars/').
    /// Used to construct URL responses for avatar retrieval endpoints.
    /// Env: OCTOCON_AVATAR_PUBLIC_BASE
    /// Must be an absolute http(s) URL when set; the same rule the bootstrapper enforces
    /// on <c>config.storage.avatarPublicBase</c>.
    /// </summary>
    [AbsoluteHttpUri]
    public string? AvatarPublicBase { get; set; }
}
