namespace Interfold.Bootstrapper;

internal static class Program
{
    public static Task<int> Main(string[] args)
    {
        // CLI surface implemented in Cli/RootCli.cs.
        return Cli.RootCli.RunAsync(args);
    }
}
