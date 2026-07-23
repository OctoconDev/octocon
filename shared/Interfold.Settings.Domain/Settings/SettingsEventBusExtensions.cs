using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Settings;

// Intent-named publish helpers on IClusterEventBus for settings events. Publish-only —
// orchestration lives in SettingsIdempotentCommandFlow.
internal static class SettingsEventBusExtensions
{
    /// <summary>includeUsername → EmitUsernameUpdated on the event → socket handlers fan
    /// a <c>username_updated</c> frame.</summary>
    public static ValueTask PublishProfileUpdatedAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId principalId,
        bool includeUsername,
        CancellationToken cancellationToken = default)
        => eventBus.PublishAsync(new SettingsProfileUpdatedEvent(principalId, includeUsername), cancellationToken);
}
