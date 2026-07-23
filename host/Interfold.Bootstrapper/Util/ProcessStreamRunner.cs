using System.Diagnostics;
using System.Text;

namespace Interfold.Bootstrapper.Util;

/// <summary>
/// Thin lifecycle wrapper for processes whose stdout/stdin is piped as a raw stream (e.g.
/// database dump/restore). The caller supplies a <paramref name="pipe"/> delegate that wires
/// the directional I/O; this helper owns startup, stderr capture, kill-on-cancel, and
/// exit-code validation.
/// </summary>
internal static class ProcessStreamRunner
{
    /// <param name="fileName">Executable to run.</param>
    /// <param name="arguments">Arguments for the process.</param>
    /// <param name="environment">Optional environment variable overrides.</param>
    /// <param name="redirectStandardInput">Whether to redirect stdin (required for file→stdin pipes).</param>
    /// <param name="forwardStdout">
    ///   When <c>true</c>, stdout is read via <see cref="Process.BeginOutputReadLine"/> and
    ///   forwarded to <see cref="Console.WriteLine"/>. Use for stdin-direction pipes where the
    ///   process may emit progress on stdout.
    /// </param>
    /// <param name="pipe">
    ///   Delegate that performs the directional I/O (e.g. stdout→file or file→stdin).
    ///   Receives the started <see cref="Process"/> so it can access
    ///   <see cref="Process.StandardOutput"/> or <see cref="Process.StandardInput"/>.
    /// </param>
    /// <param name="ct">Cancellation token; cancellation kills the entire process tree.</param>
    public static async Task RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IDictionary<string, string?>? environment,
        bool redirectStandardInput,
        bool forwardStdout,
        Func<Process, Task> pipe,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStandardInput,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        if (environment is not null)
        {
            foreach (var kvp in environment)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }
        }

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        if (forwardStdout)
        {
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
        }

        if (!proc.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }
        proc.BeginErrorReadLine();
        if (forwardStdout)
        {
            proc.BeginOutputReadLine();
        }

        try
        {
            await pipe(proc).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', arguments)} exited {proc.ExitCode}: {stderr.ToString().Trim()}");
        }
    }
}
