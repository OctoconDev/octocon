using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Abstractions;

/// <summary>Pushes FCM notifications on fronting state changes (mirrors legacy
/// <c>Octocon.Global.FrontNotifier</c>).</summary>
public interface IFCMService
{
    /// <summary>Fans a fronting-changed notification to every friend of the system.</summary>
    Task NotifyFrontingChangedAsync(
        SystemId systemId,
        IReadOnlyList<AlterId> currentAlterIds,
        CancellationToken cancellationToken = default);
}
