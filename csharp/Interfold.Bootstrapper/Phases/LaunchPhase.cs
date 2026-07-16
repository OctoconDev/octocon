using System.Net;
using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Interfold.Contracts.Configuration;
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
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(5);

    public static async Task RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        const string Phase = "launch";
        logger.PhaseStart(Phase);

        var composeFile = FindComposeFile(options.OutputDir);
        if (composeFile is null)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.NoComposeFile);
            throw new InvalidOperationException(
                $"docker-compose.yaml not found under {options.OutputDir}. Run `bootstrap publish` first.");
        }
        logger.Info($"    using compose file {composeFile}");

        var apiHttpPort = await ResolveApiHttpPortAsync(options, ct).ConfigureAwait(false);

        if (await IsCassandraDeploymentAsync(options, ct).ConfigureAwait(false))
        {
            await EnsureBuiltAsync(logger, ct).ConfigureAwait(false);
        }

        await DockerComposeUpAsync(composeFile, logger, ct).ConfigureAwait(false);

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

    private static string? FindComposeFile(string outputDir) => BootstrapArtifactPaths.FindComposeFile(outputDir);

    private static async Task<int> ResolveApiHttpPortAsync(BootstrapOptions options, CancellationToken ct)
    {
        // Re-read the persisted bootstrap config so this phase is self-sufficient when invoked as
        // `bootstrap up` against an already-generated stack.
        var configPath = BootstrapArtifactPaths.ResolveConfigPath(options);
        if (File.Exists(configPath))
        {
            var json = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false);
            var cfg = JsonSerializer.Deserialize(json, BootstrapJsonContext.Default.BootstrapConfig);
            if (cfg is not null) return cfg.Ports.ApiHttp;
        }
        return 5000;
    }

    private static async Task<bool> IsCassandraDeploymentAsync(BootstrapOptions options, CancellationToken ct)
    {
        var configPath = BootstrapArtifactPaths.ResolveConfigPath(options);
        if (!File.Exists(configPath))
        {
            return false;
        }

        try
        {
            var json = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false);
            var cfg = JsonSerializer.Deserialize(json, BootstrapJsonContext.Default.BootstrapConfig);
            return cfg is not null && IsCassandraDeployment(cfg);
        }
        catch
        {
            return false;
        }
    }

    private static async Task DockerComposeUpAsync(string composeFile, PhaseLogger logger, CancellationToken ct)
    {
        logger.Info("    docker compose up -d ...");
        var up = await ProcessRunner.RunAsync("docker",
            ["compose", "-f", composeFile, "up", "-d"], ct: ct).ConfigureAwait(false);
        if (up.ExitCode != 0)
        {
            logger.Error(up.StdErr.Trim());
            throw new InvalidOperationException($"docker compose up exited with code {up.ExitCode}.");
        }
        if (!string.IsNullOrWhiteSpace(up.StdOut)) logger.Info(up.StdOut.Trim());
    }

    private static async Task WaitForApiHealthyAsync(int apiHttpPort, PhaseLogger logger, CancellationToken ct)
    {
        // Use a per-attempt 5s timeout so a hung TCP connect doesn't burn the whole budget on one probe.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var url = $"http://localhost:{apiHttpPort}{HealthEndpoints.Ready}";
        logger.Info($"    polling {url} (up to {HealthTimeout.TotalMinutes:F0}m)");

        var deadline = DateTime.UtcNow + HealthTimeout;
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    logger.Info($"    api healthy after {attempt} attempt(s)");
                    return;
                }
                logger.Info($"    attempt {attempt}: {(int)resp.StatusCode} {resp.StatusCode}");
            }
            catch (HttpRequestException)
            {
                // Expected during compose startup; the API container may not be listening yet.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-request timeout - keep going.
            }

            try
            {
                await Task.Delay(HealthPollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        throw new TimeoutException(
            $"interfold-api did not become healthy at {url} within {HealthTimeout.TotalMinutes:F0} minutes.");
    }

    private static async Task DumpComposeLogsAsync(string composeFile, PhaseLogger logger, CancellationToken ct)
    {
        logger.Warn("dumping compose logs for diagnosis...");
        var logs = await ProcessRunner.RunAsync("docker",
            ["compose", "-f", composeFile, "logs", "--tail", "200"], ct: ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(logs.StdOut)) Console.WriteLine(logs.StdOut);
        if (!string.IsNullOrWhiteSpace(logs.StdErr)) Console.Error.WriteLine(logs.StdErr);
    }
}
