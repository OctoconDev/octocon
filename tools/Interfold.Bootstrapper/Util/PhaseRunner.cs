using Interfold.Bootstrapper.Cli;

namespace Interfold.Bootstrapper.Util;

internal static class PhaseRunner
{
    public static async Task<ProcessRunResult> RunOrPhaseFailAsync(
        Func<Task<ProcessRunResult>> op,
        PhaseLogger logger,
        string phase,
        string reason,
        string failurePrefix,
        CancellationToken ct)
    {
        var result = await op();
        if (result.ExitCode != 0)
        {
            logger.PhaseFail(phase, reason);
            throw new InvalidOperationException(
                $"{failurePrefix} exited {result.ExitCode}: {result.StdErr.Trim()}");
        }
        return result;
    }
}
