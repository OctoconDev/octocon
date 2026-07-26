namespace Interfold.Bootstrapper.Util;

internal static class DockerComposeExec
{
    public static async Task<ProcessRunResult> RunAsync(
        string composeFile,
        string service,
        string command,
        IReadOnlyList<string>? args = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? stdin = null,
        CancellationToken ct = default)
    {
        var execArgs = new List<string> { "compose", "-f", composeFile, "exec", "-T" };

        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                execArgs.Add("-e");
                execArgs.Add($"{key}={value}");
            }
        }

        execArgs.Add(service);
        execArgs.Add(command);

        if (args is not null)
        {
            execArgs.AddRange(args);
        }

        return await ProcessRunner.RunAsync("docker", execArgs, stdin: stdin, ct: ct).ConfigureAwait(false);
    }
}
