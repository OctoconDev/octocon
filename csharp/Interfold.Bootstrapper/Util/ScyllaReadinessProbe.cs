using Interfold.Bootstrapper.Cli;
using Interfold.DatabaseBootstrap;

namespace Interfold.Bootstrapper.Util;

/// <summary>
/// Scylla-side sister to <see cref="PostgresReadinessProbe"/>. Mirrors the same
/// options-record + probe-loop shape so both readiness surfaces stay legible side by side.
///
/// <para>
/// Where the postgres probe polls <c>pg_isready</c> through <c>docker compose exec</c>,
/// this one drives a CQL round-trip via <see cref="IScyllaExecutor.TryExecCqlAsync"/> —
/// <c>DESCRIBE CLUSTER</c> is cheap, requires an authenticated session, and by definition
/// only succeeds once the node has finished gossip-bootstrap and is accepting auth. The
/// probe tries the app credentials first, then falls back to the built-in
/// <c>cassandra/cassandra</c> pair; either successful response is a green.
/// </para>
///
/// <para>
/// Previously open-coded as <c>WaitForScyllaAsync</c> at
/// <c>DatabaseInitPhase.cs:176</c>. Extraction lets any future Scylla-mode caller
/// (restore, integration harness) reuse the same policy without duplicating the
/// two-credential fallback + attempt-count logging.
/// </para>
/// </summary>
internal static class ScyllaReadinessProbe
{
    public static async Task WaitAsync(
        IScyllaExecutor executor,
        ScyllaSeedOptions credentials,
        ScyllaReadinessOptions options,
        PhaseLogger logger,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(options.Timeout);
        var attempt = 0;
        var consecutiveSuccesses = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            var asApp = await executor.TryExecCqlAsync(
                credentials.AppUser, credentials.AppPassword, "DESCRIBE CLUSTER", ct).ConfigureAwait(false);
            if (asApp.Succeeded)
            {
                consecutiveSuccesses++;
                if (consecutiveSuccesses >= options.RequiredConsecutiveSuccesses)
                {
                    logger.Info($"    scylla ready (as app user) after {attempt} attempt(s)");
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                continue;
            }

            var asDefault = await executor.TryExecCqlAsync(
                ScyllaCqlTemplates.DefaultUser, ScyllaCqlTemplates.DefaultPassword,
                "DESCRIBE CLUSTER", ct).ConfigureAwait(false);
            if (asDefault.Succeeded)
            {
                consecutiveSuccesses++;
                if (consecutiveSuccesses >= options.RequiredConsecutiveSuccesses)
                {
                    logger.Info($"    scylla ready (as cassandra default) after {attempt} attempt(s)");
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                continue;
            }

            if (consecutiveSuccesses > 0)
                consecutiveSuccesses = 0;

            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }

        throw new TimeoutException($"scylla did not become ready within {options.Timeout.TotalMinutes} minutes.");
    }
}
