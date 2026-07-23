namespace Interfold.Shared.Contracts.Configuration;

/// <summary>API health-probe routes shared by ServiceDefaults, AppHost, and bootstrapper.
/// External contract — changing any value here is a breaking change for operators.</summary>
public static class HealthEndpoints
{
    public const string PathPrefix = "/health";

    /// <summary>Liveness: process is up; no dependency checks.</summary>
    public const string Live = "/health/live";

    /// <summary>Readiness: checks tagged <see cref="HealthCheckTags.Ready"/> pass.</summary>
    public const string Ready = "/health/ready";

    /// <summary>Startup: checks tagged <see cref="HealthCheckTags.Startup"/> pass.</summary>
    public const string Startup = "/health/startup";
}

/// <summary>Tag spellings that route an <c>IHealthCheck</c> onto a HealthEndpoints route.</summary>
public static class HealthCheckTags
{
    public const string Live = "live";
    public const string Ready = "ready";
    public const string Startup = "startup";
}

/// <summary>Well-known <c>IHealthCheck</c> registration names.</summary>
public static class HealthCheckNames
{
    /// <summary>ServiceDefaults' built-in "always healthy" liveness check.</summary>
    public const string Self = "self";
}
