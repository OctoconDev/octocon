using Interfold.Bootstrapper.Util;
using Interfold.DatabaseBootstrap;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// <see cref="IScyllaExecutor"/> implementation that shells out to
/// <c>docker compose -f &lt;file&gt; exec -T &lt;service&gt; cqlsh ...</c>. Used by
/// <see cref="DatabaseInitPhase"/> in the self-hosted deployment path.
/// </summary>
internal sealed class ComposeExecScyllaExecutor(string composeFile, string scyllaService, IDatabaseInitLogger logger)
    : IScyllaExecutor
{
    public async Task ExecCqlAsync(string user, string password, string cql, CancellationToken ct)
    {
        var result = await RunCqlshAsync(user, password, cql, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            logger.Error($"cqlsh on {scyllaService} as {user} failed: {result.StdErr.Trim()}");
            throw new InvalidOperationException(
                $"cqlsh command failed inside {scyllaService} (exit {result.ExitCode}).");
        }
    }

    public async Task<ScyllaExecResult> TryExecCqlAsync(string user, string password, string cql, CancellationToken ct)
    {
        var result = await RunCqlshAsync(user, password, cql, ct).ConfigureAwait(false);
        // We treat any non-zero exit as "didn't work" without classifying further — the
        // seed orchestrator only consumes the boolean + stdout. The bootstrapper integration
        // tests verify the cassandra-default lockdown by re-trying lockdown across reboots,
        // which exercises both the success and failure branches of this method.
        return new ScyllaExecResult(result.ExitCode == 0, result.StdOut);
    }

    private async Task<ProcessRunResult> RunCqlshAsync(string user, string password, string cql, CancellationToken ct)
    {
        // -T keeps stdin/stdout unbuffered so we capture auth-failure messages on a failed
        // connection attempt. -e takes inline CQL.
        var args = new[]
        {
            "-u", user, "-p", password,
            "-e", cql,
        };
        return await Util.DockerComposeExec.RunAsync(composeFile, scyllaService, "cqlsh", args, ct: ct).ConfigureAwait(false);
    }
}
