using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Interfold.Shared.Contracts.Configuration;
using static Interfold.Bootstrapper.Phases.CassandraImagePhase;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Phase 6 — runs <c>docker compose up -d</c> against the emitted compose file and waits for the
/// API's <c>/health/ready</c> endpoint to return 200. Failure here is the most common operator-visible
/// failure mode, so the implementation logs verbosely and surfaces compose logs on timeout.
/// </summary>
internal static class LaunchPhase
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromMinutes(5);

    public static async Task RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        const string Phase = "launch";
        logger.PhaseStart(Phase);

        var composeFile = PhaseArtifactLoader.RequireComposeFileOrFail(options, logger, Phase);

        var config = await PhaseArtifactLoader.TryLoadConfigAsync(options, ct).ConfigureAwait(false);
        var apiHttpPort = config?.Ports.ApiHttp ?? 5000;

        if (config is not null && IsCassandraDeployment(config))
        {
            await EnsureBuiltAsync(logger, ct).ConfigureAwait(false);
        }

        await Util.DockerCompose.UpCheckedAsync(composeFile, services: null, logger, ct).ConfigureAwait(false);

        try
        {
            await WaitForApiHealthyAsync(apiHttpPort, logger, ct).ConfigureAwait(false);
            logger.PhaseDone(Phase);
        }
        catch (TimeoutException)
        {
            await DumpComposeLogsAsync(composeFile, logger, ct).ConfigureAwait(false);
            logger.PhaseFail(Phase, PhaseFailureReasons.HealthTimeout);
            throw;
        }
    }

    private static async Task WaitForApiHealthyAsync(int apiHttpPort, PhaseLogger logger, CancellationToken ct)
    {
        logger.Info($"    polling http://localhost:{apiHttpPort}{HealthEndpoints.Ready} (up to {HealthTimeout.TotalMinutes:F0}m)");

        var deadline = DateTime.UtcNow + HealthTimeout;
        var err = await ApiReadinessProbe.TryWaitUntilAsync(apiHttpPort, deadline, logger, ct).ConfigureAwait(false);
        if (err is not null)
        {
            throw new TimeoutException(
                $"interfold-api did not become healthy at http://localhost:{apiHttpPort}{HealthEndpoints.Ready} within {HealthTimeout.TotalMinutes:F0} minutes.");
        }
    }

    private static async Task DumpComposeLogsAsync(string composeFile, PhaseLogger logger, CancellationToken ct)
    {
        logger.Warn("dumping compose logs for diagnosis...");
        await ComposeLogDumper.DumpAsync(composeFile, services: null, tailLines: 200, logger, ct).ConfigureAwait(false);
    }
}

