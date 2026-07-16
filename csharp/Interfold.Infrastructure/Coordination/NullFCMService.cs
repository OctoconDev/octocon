using Microsoft.Extensions.Logging;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.Coordination;

/// <summary>
/// No-op <see cref="IFCMService"/> used until a real Firebase/FCM client is configured.
/// Logs at Debug level so the notification pipeline can be tested end-to-end without real tokens.
/// </summary>
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
