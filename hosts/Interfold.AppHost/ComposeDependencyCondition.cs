namespace Interfold.AppHost;

/// <summary>
/// The <c>depends_on</c> condition spellings the compose graph assigns. Values are part of
/// the Docker Compose specification (<c>condition:</c> under <c>depends_on</c>) — not ours
/// to rename.
/// </summary>
internal static class ComposeDependencyCondition
{
    /// <summary>Wait for the dependency's healthcheck to pass, not merely for its container to start.</summary>
    public const string ServiceHealthy = "service_healthy";
}
