using Interfold.Bootstrapper.Cli;

namespace Interfold.Bootstrapper.Util;

/// <summary>
/// Best-effort compose log dumper used after health-check failures to surface container
/// output for diagnosis. Previously duplicated between <see cref="Phases.LaunchPhase"/>
/// (all-service dump) and <see cref="Phases.UpdateImagesPhase"/> (per-service dump).
/// </summary>
internal static class ComposeLogDumper
{
    /// <summary>
    /// Dumps docker compose logs to stderr for operator diagnosis.
    /// <para>
    /// When <paramref name="services"/> is null or empty, runs a single
    /// <c>docker compose logs --tail {tailLines}</c> covering every service.
    /// When services are specified, iterates per-service and prefixes each block
    /// with a header so the operator can distinguish backends in the output.
    /// </para>
    /// </summary>
    public static async Task DumpAsync(
        string composeFile,
        IEnumerable<string>? services,
        int tailLines,
        PhaseLogger logger,
        CancellationToken ct)
    {
        var serviceList = services as IReadOnlyList<string>
            ?? services?.ToArray()
            ?? [];

        if (serviceList.Count == 0)
        {
            // All-service dump in a single compose call.
            var logs = await ProcessRunner.RunAsync("docker",
                ["compose", "-f", composeFile, "logs", "--tail", tailLines.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                ct: ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(logs.StdOut)) Console.Error.WriteLine(logs.StdOut);
            if (!string.IsNullOrWhiteSpace(logs.StdErr)) Console.Error.WriteLine(logs.StdErr);
        }
        else
        {
            // Per-service dump with a header so the operator can distinguish backends.
            foreach (var svc in serviceList)
            {
                var logs = await ProcessRunner.RunAsync("docker",
                    ["compose", "-f", composeFile, "logs", "--tail", tailLines.ToString(System.Globalization.CultureInfo.InvariantCulture), svc],
                    ct: ct).ConfigureAwait(false);
                if (logs.ExitCode == 0 && !string.IsNullOrWhiteSpace(logs.StdOut))
                {
                    logger.Warn($"--- {svc} logs (last {tailLines} lines) ---");
                    Console.Error.WriteLine(logs.StdOut);
                }
            }
        }
    }
}
