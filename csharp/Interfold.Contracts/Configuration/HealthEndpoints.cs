namespace Interfold.Contracts.Configuration;

/// <summary>
/// The API's health-probe routes. Shared by the ServiceDefaults endpoint mapping (the
/// writer), the AppHost's Aspire/compose health checks, and the bootstrapper's launch
/// and update-images polling (the readers), so the four can't drift.
/// </summary>
/// <remarks>
/// Values are the external contract: the published compose healthcheck, operator
/// runbooks, and reverse-proxy configs reference these exact paths. CHANGING ANY VALUE
/// HERE IS A BREAKING CHANGE for operators.
/// </remarks>
public static class HealthEndpoints
{
    /// <summary>The path segment every health route lives under. Used by consumers that
    /// treat the health surface as a whole (e.g. the tracing filter that excludes health
    /// probes from spans) rather than one specific route.</summary>
    public const string PathPrefix = "/health";

    /// <summary>Liveness: process is up; no dependency checks.</summary>
    public const string Live = "/health/live";

    /// <summary>Readiness: checks tagged <see cref="HealthCheckTags.Ready"/> pass.</summary>
    public const string Ready = "/health/ready";

    /// <summary>Startup: checks tagged <see cref="HealthCheckTags.Startup"/> pass (longer timeouts for DB init).</summary>
    public const string Startup = "/health/startup";
}

/// <summary>
/// The health-check tag spellings that route an <c>IHealthCheck</c> registration onto
/// one of the <see cref="HealthEndpoints"/> routes. The tag writer (API check
/// registrations) and the tag reader (ServiceDefaults' <c>MapHealthChecks</c>
/// predicates) must agree on these exact strings.
/// </summary>
public static class HealthCheckTags
{
    public const string Live = "live";
    public const string Ready = "ready";
    public const string Startup = "startup";
}

/// <summary>
/// Well-known <c>IHealthCheck</c> registration names shared between the writers (health-check
/// registrations) and the readers (health-check UI / dashboards). Keeping them here lets a
/// downstream operator inspect the surface without grepping the source.
/// </summary>
public static class HealthCheckNames
{
    /// <summary>The built-in "always healthy" liveness check ServiceDefaults registers.</summary>
    public const string Self = "self";
}
