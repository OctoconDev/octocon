using Interfold.Shared.Contracts.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Interfold.Api.Helpers;

/// <summary>
/// Convenience extensions for the pair of "-ready" + "-startup" checks the API
/// registers per backing store. Keeps the two timeouts in one place so a change
/// (e.g. bumping startup for slow migrations) doesn't drift one apart from the
/// other across the four persistence-mode branches in Program.cs.
/// </summary>
public static class HealthCheckExtensions
{
    /// <summary>Readiness checks fail fast (dependency dropped after boot).</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Startup checks allow longer for cold-start migrations / initial connect.</summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    public static IHealthChecksBuilder AddReadyAndStartup<T>(
        this IHealthChecksBuilder builder,
        string namePrefix)
        where T : class, IHealthCheck
    {
        builder.AddCheck<T>($"{namePrefix}-ready", tags: [HealthCheckTags.Ready], timeout: ReadyTimeout);
        builder.AddCheck<T>($"{namePrefix}-startup", tags: [HealthCheckTags.Startup], timeout: StartupTimeout);
        return builder;
    }
}
