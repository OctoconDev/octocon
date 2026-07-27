namespace Interfold.Shared.Contracts;

/// <summary>Settings-owned singleton task names.</summary>
public static class SettingsTaskNames
{
    /// <summary>Single-writer link-token issuer.</summary>
    public static readonly SingletonTaskName LinkTokenRegistry = new("link_token_registry");
}
