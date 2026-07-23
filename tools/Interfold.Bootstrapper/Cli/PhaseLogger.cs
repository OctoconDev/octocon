namespace Interfold.Bootstrapper.Cli;

/// <summary>
/// Two-channel logger used by phases.
///   * Human prose goes to stdout.
///   * Machine-readable phase status lines go to stderr when <see cref="BootstrapOptions.PrintPhaseStatus"/> is set.
/// Tests in <c>Interfold.Bootstrapper.IntegrationTests</c> read the stderr channel to assert phase
/// skip/run behavior without scraping prose logs.
/// </summary>
public sealed class PhaseLogger(BootstrapOptions options)
{
    public void Info(string message) => Console.WriteLine(message);

    public void Warn(string message) => Console.WriteLine($"[warn] {message}");

    public void Error(string message) => Console.Error.WriteLine($"[error] {message}");

    public void PhaseStart(string phase)
    {
        Console.WriteLine($"==> {phase}");
        EmitMachineStatus(phase, "start", reason: null);
    }

    public void PhaseSkip(string phase, string reason)
    {
        Console.WriteLine($"    {phase}: skipped ({reason})");
        EmitMachineStatus(phase, "skipped", reason);
    }

    public void PhaseDone(string phase)
    {
        Console.WriteLine($"    {phase}: done");
        EmitMachineStatus(phase, "done", reason: null);
    }

    public void PhaseFail(string phase, string reason)
    {
        Console.WriteLine($"    {phase}: FAILED — {reason}");
        EmitMachineStatus(phase, "failed", reason);
    }

    private void EmitMachineStatus(string phase, string status, string? reason)
    {
        if (!options.PrintPhaseStatus)
        {
            return;
        }

        // Single-line, space-separated, no quoting needed because reasons are short identifiers.
        var line = reason is null
            ? $"phase={phase} status={status}"
            : $"phase={phase} status={status} reason={reason}";
        Console.Error.WriteLine(line);
    }
}
