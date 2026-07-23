namespace Interfold.Contracts.Configuration;

using System.ComponentModel.DataAnnotations;
using Interfold.Contracts;
using Interfold.Contracts.Configuration.Validation;

/// <summary>Scylla/Postgres persistence + retry configuration. Bound from OCTOCON_
/// env vars. <c>ScyllaKeyspace</c> unifies session default keyspace and region routing;
/// Scylla connection secrets live in <c>internal.secrets</c> and are read via
/// <c>ISecretsStore</c>.</summary>
public sealed class PersistenceConfiguration : IValidatableObject
{
    public const string SectionName = "Octocon:Persistence";

    [EnumDataType(typeof(PersistenceMode))]
    public PersistenceMode Mode { get; set; } = PersistenceMode.ScyllaPostgres;

    /// <summary>Region/keyspace identity — nam|eur|ocn|eas|sam|sas|gdpr.
    /// Env: OCTOCON_SCYLLA_KEYSPACE.</summary>
    public Enums.ScyllaKeyspace ScyllaKeyspace { get; set; } = Enums.ScyllaKeyspace.Nam;

    /// <summary>Env: OCTOCON_POSTGRES_CONNECTION.</summary>
    [Required, MinLength(1)]
    public string PostgresConnectionString { get; set; } = "";

    /// <summary>True → migration service creates only the ScyllaKeyspace keyspace.
    /// Env: OCTOCON_SINGLE_SCYLLA_INSTANCE.</summary>
    public bool IsSingleScyllaInstance { get; set; } = false;

    /// <summary>Env: OCTOCON_DB_RETRY_ATTEMPTS.</summary>
    [Range(ConfigurationBounds.DbRetryAttemptsMin, ConfigurationBounds.DbRetryAttemptsMax)]
    public int DbRetryAttempts { get; set; } = 3;

    /// <summary>Env: OCTOCON_DB_RETRY_INITIAL_DELAY_MS (binder converts ms → TimeSpan).</summary>
    public TimeSpan DbRetryInitialDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Env: OCTOCON_DB_RETRY_MAX_DELAY_MS. Must be ≥ DbRetryInitialDelay.</summary>
    public TimeSpan DbRetryMaxDelay { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Env: OCTOCON_HYDRATION_MAX_CONCURRENCY. Caps friendship-query fan-out.</summary>
    [Range(ConfigurationBounds.HydrationMaxConcurrencyMin, ConfigurationBounds.HydrationMaxConcurrencyMax)]
    public int HydrationMaxConcurrency { get; set; } = 8;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var initialMs = DbRetryInitialDelay.TotalMilliseconds;
        if (initialMs < ConfigurationBounds.DbRetryInitialDelayMsMin
            || initialMs > ConfigurationBounds.DbRetryInitialDelayMsMax)
        {
            yield return new ValidationResult(
                $"{nameof(DbRetryInitialDelay)}={initialMs}ms is outside the allowed " +
                $"[{ConfigurationBounds.DbRetryInitialDelayMsMin}..{ConfigurationBounds.DbRetryInitialDelayMsMax}]ms range.",
                [nameof(DbRetryInitialDelay)]);
        }

        var maxMs = DbRetryMaxDelay.TotalMilliseconds;
        if (maxMs < ConfigurationBounds.DbRetryMaxDelayMsMin
            || maxMs > ConfigurationBounds.DbRetryMaxDelayMsMax)
        {
            yield return new ValidationResult(
                $"{nameof(DbRetryMaxDelay)}={maxMs}ms is outside the allowed " +
                $"[{ConfigurationBounds.DbRetryMaxDelayMsMin}..{ConfigurationBounds.DbRetryMaxDelayMsMax}]ms range.",
                [nameof(DbRetryMaxDelay)]);
        }

        if (DbRetryMaxDelay < DbRetryInitialDelay)
        {
            yield return new ValidationResult(
                $"{nameof(DbRetryMaxDelay)} ({DbRetryMaxDelay}) must be >= {nameof(DbRetryInitialDelay)} ({DbRetryInitialDelay}).",
                [nameof(DbRetryMaxDelay), nameof(DbRetryInitialDelay)]);
        }
    }
}
