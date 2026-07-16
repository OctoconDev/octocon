namespace Interfold.Contracts.Configuration.Validation;

/// <summary>
/// Single source of truth for numeric bounds on operator-tunable <c>OCTOCON_*</c> env vars.
/// Referenced by both the bootstrapper's <c>ConfigPhase.Validate()</c> (runtime range checks
/// at operator-prompt time) and the API's <c>[Range]</c> attributes on
/// <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/> classes (DataAnnotations
/// validation at container-start time via <c>ValidateOnStart</c>).
///
/// A change on one side is a change on both — drift becomes impossible by construction.
/// If you tune a bound here, both validators pick it up on the next build.
/// </summary>
public static class ConfigurationBounds
{
    // --- Persistence tuning ---

    /// <summary>Minimum permitted value for <c>OCTOCON_DB_RETRY_ATTEMPTS</c>.</summary>
    public const int DbRetryAttemptsMin = 1;

    /// <summary>Maximum permitted value for <c>OCTOCON_DB_RETRY_ATTEMPTS</c>.</summary>
    public const int DbRetryAttemptsMax = 100;

    /// <summary>Minimum permitted value in milliseconds for <c>OCTOCON_DB_RETRY_INITIAL_DELAY_MS</c>.</summary>
    public const int DbRetryInitialDelayMsMin = 1;

    /// <summary>Maximum permitted value in milliseconds for <c>OCTOCON_DB_RETRY_INITIAL_DELAY_MS</c> (60 s).</summary>
    public const int DbRetryInitialDelayMsMax = 60_000;

    /// <summary>Minimum permitted value in milliseconds for <c>OCTOCON_DB_RETRY_MAX_DELAY_MS</c>.</summary>
    public const int DbRetryMaxDelayMsMin = 1;

    /// <summary>Maximum permitted value in milliseconds for <c>OCTOCON_DB_RETRY_MAX_DELAY_MS</c> (10 min).</summary>
    public const int DbRetryMaxDelayMsMax = 600_000;

    /// <summary>Minimum permitted value for <c>OCTOCON_HYDRATION_MAX_CONCURRENCY</c>.</summary>
    public const int HydrationMaxConcurrencyMin = 1;

    /// <summary>Maximum permitted value for <c>OCTOCON_HYDRATION_MAX_CONCURRENCY</c>.</summary>
    public const int HydrationMaxConcurrencyMax = 1024;

    // --- Socket tuning ---

    /// <summary>Minimum permitted value in bytes for <c>OCTOCON_SOCKET_BATCH_BYTES_THRESHOLD</c>.</summary>
    public const int SocketBatchBytesThresholdMin = 1;

    /// <summary>Maximum permitted value in bytes for <c>OCTOCON_SOCKET_BATCH_BYTES_THRESHOLD</c> (16 MiB).</summary>
    public const int SocketBatchBytesThresholdMax = 16 * 1024 * 1024;
}
