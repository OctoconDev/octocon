using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Builds the custom Cassandra 5 image the AppHost declares via
/// <c>AddDockerfile("cassandra", "../../db/cassandra")</c>. Aspire's compose publisher
/// replaces Dockerfile services with an <c>${CASSANDRA_IMAGE}</c> placeholder in publish
/// mode — the bootstrapper must build the image locally and fill that .env entry before
/// <c>docker compose up</c> can start the stack.
/// </summary>
internal static class CassandraImagePhase
{
    /// <summary>
    /// Local-only tag written to <c>CASSANDRA_IMAGE</c> in the emitted <c>.env</c>.
    /// </summary>
    internal const string LocalImageTag = "interfold-cassandra:local";

    internal static bool IsCassandraDeployment(BootstrapConfig config) =>
        config.DatabaseMode == DatabaseMode.Cassandra;

    internal static string DockerfileContextPath =>
        Path.Combine(AppContext.BaseDirectory, "db", "cassandra");

    /// <summary>
    /// Builds <see cref="LocalImageTag"/> from the Dockerfile embedded next to the bootstrapper
    /// binary. Idempotent — Docker's layer cache makes repeat calls cheap when nothing changed.
    /// </summary>
    internal static async Task EnsureBuiltAsync(PhaseLogger logger, CancellationToken ct)
    {
        var context = DockerfileContextPath;
        var dockerfile = Path.Combine(context, "Dockerfile");
        if (!File.Exists(dockerfile))
        {
            throw new InvalidOperationException(
                $"Cassandra mode requires {dockerfile}, but it is missing. Reinstall the bootstrapper " +
                "release tarball or rerun from a checkout that includes db/cassandra/Dockerfile.");
        }

        logger.Info($"    docker build -t {LocalImageTag} {context}");
        var run = await ProcessRunner.RunAsync("docker",
            ["build", "-t", LocalImageTag, context], ct: ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            logger.Error(run.StdErr.Trim());
            throw new InvalidOperationException(
                $"docker build for Cassandra failed (exit {run.ExitCode}).");
        }
        if (!string.IsNullOrWhiteSpace(run.StdOut))
        {
            logger.Info(run.StdOut.Trim());
        }
    }
}
