using Microsoft.Extensions.Logging;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Infrastructure.Coordination;

/// <summary>No-op <see cref="IFCMService"/> for deployments without a configured FCM
/// credential. Debug-level log so the notification pipeline is testable without real tokens.</summary>
public sealed class NullFCMService(ILogger<NullFCMService> logger) : IFCMService
{
    public Task NotifyFrontingChangedAsync(
        SystemId systemId,
        IReadOnlyList<AlterId> currentAlterIds,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug(
            "FCM stub: fronting changed for system={SystemId}, alters=[{AlterIds}]",
            systemId, string.Join(",", currentAlterIds));

        return Task.CompletedTask;
    }
}
