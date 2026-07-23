using Interfold.Bootstrapper.Util;
using Interfold.DatabaseBootstrap;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// <see cref="IPostgresExecutor"/> implementation that shells out to
/// <c>docker compose -f &lt;file&gt; exec -T &lt;service&gt; psql ...</c>. Used by
/// <see cref="DatabaseInitPhase"/> in the self-hosted deployment path.
/// </summary>
/// <remarks>
/// <para>
/// Holds the retry budget and transient-error classifier here (not in
/// <see cref="DatabaseInitPhase"/>) so the shared orchestrator only ever sees a clean
/// "success or fatal" outcome — the messy "is shutting down" / "is starting up" window the
/// postgres entrypoint goes through on first boot is absorbed locally.
/// </para>
/// <para>
/// With 20 attempts * 5s = 100s of patience we cover the worst observed checkpoint+sync
/// window (~45s on contended DinD-on-WSL2 storage) with margin to spare, without papering
/// over genuine SQL errors (those return non-transient on the next attempt).
/// </para>
/// </remarks>
internal sealed class ComposeExecPostgresExecutor(string composeFile, string postgresService, IDatabaseInitLogger logger)
    : IPostgresExecutor
{
    private const int PsqlRetryAttempts = 20;
    private static readonly TimeSpan PsqlRetryDelay = TimeSpan.FromSeconds(5);

    public Task ExecScriptAsync(string user, string password, string database, string sql, CancellationToken ct)
        => RunPsqlWithRetryAsync(user, password, database, sql, vars: null, ct);

    public Task ExecScriptWithVarsAsync(
        string user, string password, string database, string sql,
        IReadOnlyList<(string Name, string Value)> vars, CancellationToken ct)
        => RunPsqlWithRetryAsync(user, password, database, sql, vars, ct);

    public async Task<string?> ExecScalarAsync(string user, string password, string database, string sql, CancellationToken ct)
    {
        // Scalar probes do NOT retry on transient errors — the caller (PostgresSeeder) wraps
        // the call site in try/catch and treats any failure as "not yet initialised". Adding
        // a retry budget here would make idempotency probes wait 100s on a fresh cluster
        // before falling back to "treat as uninitialised", which is the opposite of what we
        // want.
        var result = await RunPsqlOnceAsync(user, password, database, sql, vars: null, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            // Return null instead of throwing so probe callers behave consistently with the
            // "empty result" case. Genuine misuse (auth failure with the wrong password) is
            // surfaced upstream as "not yet initialised" — same outcome as the original
            // bootstrap script's behaviour.
            return null;
        }
        var line = result.StdOut.Trim();
        return string.IsNullOrEmpty(line) ? null : line;
    }

    private async Task RunPsqlWithRetryAsync(
        string user, string password, string database, string sql,
        IReadOnlyList<(string Name, string Value)>? vars, CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < PsqlRetryAttempts; attempt++)
        {
            var result = await RunPsqlOnceAsync(user, password, database, sql, vars, ct).ConfigureAwait(false);
            if (result.ExitCode == 0) return;

            var err = result.StdErr.Trim();
            if (IsTransientPostgresError(err))
            {
                logger.Info(
                    $"    transient psql error (attempt {attempt + 1}/{PsqlRetryAttempts}): {FirstLine(err)}");
                lastError = new InvalidOperationException(err);
                await Task.Delay(PsqlRetryDelay, ct).ConfigureAwait(false);
                continue;
            }

            logger.Error($"psql {database} as {user} failed: {err}");
            throw new InvalidOperationException(
                $"psql script failed inside {postgresService} (exit {result.ExitCode}).");
        }

        logger.Error($"psql {database} as {user} kept hitting transient errors: {lastError?.Message}");
        throw new InvalidOperationException(
            $"psql script failed after {PsqlRetryAttempts} retries inside {postgresService}.", lastError);
    }

    private async Task<ProcessRunResult> RunPsqlOnceAsync(
        string user, string password, string database, string sql,
        IReadOnlyList<(string Name, string Value)>? vars, CancellationToken ct)
    {
        // -e PGPASSWORD avoids putting the password on the visible command line.
        // -v ON_ERROR_STOP=1 turns the first SQL error into a non-zero exit (otherwise psql silently continues past
        // errors which would mask half-broken bootstraps). -t -A trim leading whitespace
        // and the column-alignment padding for scalar probes.
        
        var env = new Dictionary<string, string> { { DatabaseArchiveStreamer.PgPasswordEnvVar, password } };
        
        var args = new List<string>
        {
            "-U", user, "-d", database,
            "-v", "ON_ERROR_STOP=1",
        };
        
        if (vars is not null)
        {
            foreach (var (name, value) in vars)
            {
                args.Add("-v");
                args.Add($"{name}={value}");
            }
        }
        args.Add("-t");
        args.Add("-A");

        return await Util.DockerComposeExec.RunAsync(
            composeFile, postgresService, "psql", args, env, stdin: sql, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Transition states postgres moves through while the entrypoint is bouncing it, plus
    /// the classic CREATE DATABASE race against template1. Auth errors, syntax errors etc.
    /// are NOT transient and surface as immediate failures.
    /// </summary>
    private static bool IsTransientPostgresError(string stderr)
    {
        return stderr.Contains("the database system is shutting down", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("the database system is starting up", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("could not connect to server", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("is being accessed by other users", StringComparison.OrdinalIgnoreCase)
            // Sent to all live connections when postgres receives SIGTERM (fast-shutdown).
            // ERRCODE_ADMIN_SHUTDOWN (57P01); a follow-up connect will see "shutting down"
            // first, then "starting up", then succeed once normal mode comes up.
            || stderr.Contains("terminating connection due to administrator command", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstLine(string s)
    {
        var idx = s.IndexOfAny(['\r', '\n']);
        return idx < 0 ? s : s[..idx];
    }
}
