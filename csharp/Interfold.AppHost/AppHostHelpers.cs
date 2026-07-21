using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Net.Sockets;

namespace Interfold.AppHostGraph;

/// <summary>
/// A synchronous <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck"/>
/// factory that opens a raw TCP socket to a host:port and reports
/// <see cref="HealthStatus.Healthy"/> on connect, <see cref="HealthStatus.Unhealthy"/>
/// on any exception. Used by the AppHost's dashboard-facing readiness gates for
/// containers that don't expose an HTTP health endpoint (Postgres 5432, the
/// Cassandra-only-mode CQL port). The Scylla path uses per-node <c>docker exec</c>
/// probes instead (see <see cref="DockerExecCqlProbe"/>).
/// </summary>
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

/// <summary>
/// Factory helpers for the compose <see cref="Healthcheck"/> objects attached to
/// each service via <c>PublishAsDockerComposeService</c>. Preserves the compose
/// <c>$${VAR}</c> escape trick — the helper never touches the command string, so
/// callers can still emit literal <c>$VAR</c> for the container's own shell to
/// expand at runtime (rather than docker-compose interpolating it at YAML-parse
/// time against the host shell).
/// </summary>
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

/// <summary>
/// Aspire resource-builder extension that captures the "named volume + persistent
/// container lifetime" pair we attach to Postgres, each Scylla node, and Cassandra
/// when the operator opts into persistent containers. Keeping the two calls
/// together avoids the (subtle) foot-gun of adding one but forgetting the other:
/// a named volume without <see cref="ContainerLifetime.Persistent"/> gets
/// re-created on every <c>docker compose up</c>, which silently blows away the
/// data the volume was supposed to preserve.
/// </summary>
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
