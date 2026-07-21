using Interfold.Contracts.Events;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Settings;

/// <summary>
/// Intent-named publish helpers on <see cref="IClusterEventBus"/> for settings-shaped
/// events. Extensions so command-handler bodies read as
/// <c>_eventBus.PublishProfileUpdatedAsync(command.PrincipalId, includeUsername: true, ct)</c>
/// rather than a helper-first spelling — the noun the caller cares about (the bus) sits
/// at the front.
///
/// <para>
/// Named "…EventBusExtensions" (not "…EventFlow" or "…PublisherHelper") to signal that
/// every method here is a thin, side-effect-only publish to the cluster bus with no
/// orchestration, idempotency, or DB access. Anything more complex belongs in the
/// per-handler <see cref="SettingsIdempotentCommandFlow"/> mutate → publish body.
/// </para>
/// </summary>
internal static class SettingsEventBusExtensions
{
    /// <summary>
    /// Publishes <see cref="SettingsProfileUpdatedEvent"/> for <paramref name="principalId"/>.
    /// <paramref name="includeUsername"/> becomes <c>EmitUsernameUpdated</c> on the event so
    /// downstream socket handlers know whether to fan a <c>username_updated</c> frame — the
    /// same flag every profile-mutation handler used to encode inline at the publish site.
    /// </summary>
    public static ValueTask PublishProfileUpdatedAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId principalId,
        bool includeUsername,
        CancellationToken cancellationToken = default)
        => eventBus.PublishAsync(new SettingsProfileUpdatedEvent(principalId, includeUsername), cancellationToken);
}
