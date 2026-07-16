using Interfold.Contracts.Ids;

namespace Interfold.Domain.Abstractions;

/// <summary>
/// Contract for pushing FCM notifications when fronting state changes.
/// Mirrors <c>Octocon.Global.FrontNotifier</c> push behaviour from the legacy Elixir runtime.
/// </summary>
public interface IFCMService
{
    /// <summary>
    /// Notifies all friends of <paramref name="systemId"/> that fronting state has changed.
    /// </summary>
    /// <param name="systemId">The system whose front changed.</param>
    /// <param name="currentAlterIds">The alter IDs currently fronting (may be empty if all ended).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task NotifyFrontingChangedAsync(
        SystemId systemId,
        IReadOnlyList<AlterId> currentAlterIds,
        CancellationToken cancellationToken = default);
}
