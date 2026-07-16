using Cassandra;
using Interfold.Contracts.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Interfold.Infrastructure.Persistence;

internal static class DatabaseTransientRetry
{
    private const int MaxBackoffExponent = 8;

    public static Task ExecuteScyllaAsync(
        Func<Task> operation,
        PersistenceConfiguration options,
        CancellationToken cancellationToken = default,
        ILogger? logger = null
    ) => ExecuteAsync(operation, options, IsScyllaTransient, cancellationToken, logger);

    public static Task<T> ExecuteScyllaAsync<T>(
        Func<Task<T>> operation,
        PersistenceConfiguration options,
        CancellationToken cancellationToken = default,
        ILogger? logger = null
    ) => ExecuteAsync(operation, options, IsScyllaTransient, logger, cancellationToken);

    public static Task ExecutePostgresAsync(
        Func<Task> operation,
        PersistenceConfiguration options,
        CancellationToken cancellationToken = default,
        ILogger? logger = null
    ) => ExecuteAsync(operation, options, IsPostgresTransient, cancellationToken, logger);

    public static Task<T> ExecutePostgresAsync<T>(
        Func<Task<T>> operation,
        PersistenceConfiguration options,
        CancellationToken cancellationToken = default,
        ILogger? logger = null
    ) => ExecuteAsync(operation, options, IsPostgresTransient, logger, cancellationToken);

    private static async Task ExecuteAsync(
        Func<Task> operation,
        PersistenceConfiguration options,
        Func<Exception, bool> isTransient,
        CancellationToken cancellationToken,
        ILogger? logger
    )
    {
        await ExecuteAsync(async () =>
        {
            await operation();
            return true;
        }, options, isTransient, logger, cancellationToken);
    }

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation,
        PersistenceConfiguration options,
        Func<Exception, bool> isTransient,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, options.DbRetryAttempts);
        var initialDelay = options.DbRetryInitialDelay <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(1)
            : options.DbRetryInitialDelay;
        var maxDelay = options.DbRetryMaxDelay < initialDelay
            ? initialDelay
            : options.DbRetryMaxDelay;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation();
            }
            catch (Exception ex) when (attempt < attempts && isTransient(ex))
            {
                var delay = ComputeBackoff(attempt, initialDelay, maxDelay);
                logger?.LogWarning(ex,
                    "Transient database error on attempt {Attempt}/{MaxAttempts}; retrying in {DelayMs} ms.",
                    attempt, attempts, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex,
                    "Database operation failed on attempt {Attempt}/{MaxAttempts}.",
                    attempt, attempts);
                throw;
            }
        }
    }

    private static TimeSpan ComputeBackoff(int attempt, TimeSpan initialDelay, TimeSpan maxDelay)
    {
        var exponent = Math.Min(attempt - 1, MaxBackoffExponent);
        var baseTicks = initialDelay.Ticks * (1L << exponent);
        var cappedTicks = Math.Min(baseTicks, maxDelay.Ticks);
        // Jitter over [capped/2, capped] to spread reconnect storms without ever waiting
        // longer than the configured cap.
        var lower = cappedTicks / 2L;
        var jitteredTicks = Random.Shared.NextInt64(lower, cappedTicks + 1);
        return TimeSpan.FromTicks(jitteredTicks);
    }

    private static bool IsScyllaTransient(Exception exception) => exception switch
    {
        NoHostAvailableException => true,
        OperationTimedOutException => true,
        ReadTimeoutException => true,
        WriteTimeoutException => true,
        TimeoutException => true,
        _ => false
    };

    private static bool IsPostgresTransient(Exception exception) => exception switch
    {
        NpgsqlException npgsqlException => npgsqlException.IsTransient || npgsqlException.InnerException is System.IO.EndOfStreamException,
        TimeoutException => true,
        _ => false
    };
}
