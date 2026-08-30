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

    /// <summary>Argv for <c>docker build -t … -f - &lt;contextDir&gt;</c> with Dockerfile on stdin.</summary>
    internal static IReadOnlyList<string> BuildDockerBuildArgs(string contextDir) =>
        ["build", "-t", LocalImageTag, "-f", "-", contextDir];

    /// <summary>
    /// Builds <see cref="LocalImageTag"/> from the embedded Dockerfile. Idempotent — Docker's
    /// layer cache makes repeat calls cheap when nothing changed.
    /// </summary>
    internal static async Task EnsureBuiltAsync(PhaseLogger logger, CancellationToken ct)
    {
        var dockerfile = EmbeddedSupportFiles.ReadAllText(EmbeddedSupportFiles.CassandraDockerfileRelative);
        var contextDir = Path.Combine(Path.GetTempPath(), $"interfold-cassandra-ctx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contextDir);

        try
        {
            logger.Info($"    docker build -t {LocalImageTag} -f - {contextDir}");
            var run = await ProcessRunner.RunAsync(
                "docker",
                BuildDockerBuildArgs(contextDir),
                stdin: dockerfile,
                ct: ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
            {
                logger.Error(run.StdErr.Trim());
                throw new InvalidOperationException(
                    $"docker build for Cassandra failed (exit {run.ExitCode}).");
            }

            if (!string.IsNullOrWhiteSpace(run.StdOut))
                logger.Info(run.StdOut.Trim());
        }
        finally
        {
            try { Directory.Delete(contextDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
