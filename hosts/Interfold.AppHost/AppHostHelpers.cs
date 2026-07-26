using System.Net.Sockets;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Interfold.AppHost;

/// <summary>Raw TCP-connect <see cref="IHealthCheck"/> factory for the AppHost dashboard
/// readiness gates on non-HTTP containers (Postgres, Cassandra-only CQL). Scylla uses
/// <see cref="DockerExecCqlProbe"/> instead.</summary>
internal static class HostPortTcpProbe
{
    public static Func<HealthCheckResult> CreateCheck(int port, string host = "localhost")
        => () =>
        {
            try
            {
                using var tcp = new TcpClient();
                tcp.Connect(host, port);
                return HealthCheckResult.Healthy();
            }
            catch { return HealthCheckResult.Unhealthy(); }
        };
}

/// <summary>Factory helpers for compose <see cref="Healthcheck"/> objects. Passes the
/// command string through untouched so callers can rely on the <c>$$VAR</c> escape trick
/// (see the CQL probes in <see cref="InterfoldAppHost"/>).</summary>
internal static class ComposeHealthcheck
{
    public static Healthcheck CmdShell(
        string command,
        string interval,
        string timeout,
        int retries,
        string startPeriod)
        => new()
        {
            Test = ["CMD-SHELL", command],
            Interval = interval,
            Timeout = timeout,
            Retries = retries,
            StartPeriod = startPeriod,
        };

    public static Healthcheck Cmd(
        string interval,
        string timeout,
        int retries,
        string startPeriod,
        params string[] command)
        => new()
        {
            Test = ["CMD", .. command],
            Interval = interval,
            Timeout = timeout,
            Retries = retries,
            StartPeriod = startPeriod,
        };
}

/// <summary>Pairs <c>WithVolume</c> with <see cref="ContainerLifetime.Persistent"/>. A
/// named volume without persistent lifetime silently re-creates on every
/// <c>docker compose up</c>, blowing away the data it was meant to preserve.</summary>
internal static class PersistentResourceExtensions
{
    public static IResourceBuilder<T> AsPersistent<T>(
        this IResourceBuilder<T> builder,
        string volumeName,
        string containerPath)
        where T : ContainerResource
    {
        return builder
            .WithVolume(volumeName, containerPath)
            .WithLifetime(ContainerLifetime.Persistent);
    }
}
