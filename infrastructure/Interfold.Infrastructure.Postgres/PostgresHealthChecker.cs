using Interfold.Shared.Contracts.Configuration;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Interfold.Infrastructure.Postgres;

public sealed class PostgresHealthChecker : IHealthCheck
{
    private readonly IPostgresConnectionFactory _postgresConnectionFactory;
    private readonly PersistenceConfiguration _options;

    public PostgresHealthChecker(
        IPostgresConnectionFactory postgresConnectionFactory,
        IOptions<PersistenceConfiguration> options
    )
    {
        _postgresConnectionFactory = postgresConnectionFactory;
        _options = options.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = new CancellationToken())
    {
        try
        {
            await DatabaseTransientRetry.ExecutePostgresAsync(async () =>
            {
                await using var connection = await _postgresConnectionFactory.OpenConnectionAsync(cancellationToken);

                await using var pingCommand = new NpgsqlCommand("SELECT 1", connection);
                await pingCommand.ExecuteScalarAsync(cancellationToken);

                await using var tableCommand = new NpgsqlCommand(
                    "SELECT to_regclass('public.octocon_idempotency')::text",
                    connection
                );

                var table = await tableCommand.ExecuteScalarAsync(cancellationToken) as string;
                if (string.IsNullOrWhiteSpace(table))
                {
                    throw new InvalidOperationException(
                        "Postgres table 'octocon_idempotency' was not found. Run the SQL bootstrap script first."
                    );
                }
            }, _options, cancellationToken);

            return new HealthCheckResult(HealthStatus.Healthy, "Connected and required table exists.");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(HealthStatus.Unhealthy, exception: ex);
        }
    }
}
