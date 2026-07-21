using Interfold.Contracts.Events;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Fronting;

/// <summary>
/// Intent-named publish helpers on <see cref="IClusterEventBus"/> for fronting events.
/// Extensions so command-handler bodies read as
/// <c>_eventBus.PublishStateChangedAndStartedAsync(systemId, frontId, ct)</c> — the bus,
/// the noun the caller cares about, sits at the front instead of being buried as the
/// first argument to a static method.
///
/// <para>
/// Every method here bundles a related pair (or triple) of events under a single
/// intent-shaped name so the individual call site can't reorder or drop one under
/// refactor pressure. <c>PublishStateChangedAndStartedAsync</c>, for example, always
/// emits <see cref="FrontingStateChangedEvent"/> first (so subscribers can invalidate
/// caches) then <see cref="FrontingStartedEvent"/> (so subscribers can react to the
/// specific transition).
/// </para>
///
/// <para>
/// Publish-only, side-effect-only. Nothing here touches idempotency, DB, or command
/// orchestration; those live in <see cref="FrontingCommandFlow"/>.
/// </para>
/// </summary>
internal static class FrontingEventBusExtensions
{
    /// <summary>
    /// Publishes just <see cref="FrontingStateChangedEvent"/> — used by handlers that
    /// signal "something about the fronting state moved, subscribers should re-read".
    /// The "…And…" variants below all layer additional transition-specific events on top.
    /// </summary>
    public static ValueTask PublishStateChangedAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId systemId,
        CancellationToken cancellationToken = default)
        => eventBus.PublishAsync(new FrontingStateChangedEvent(systemId), cancellationToken);

    public static async ValueTask PublishStateChangedAndStartedAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId systemId,
        FrontId frontId,
        CancellationToken cancellationToken = default)
    {
        await eventBus.PublishStateChangedAsync(systemId, cancellationToken);
        await eventBus.PublishAsync(new FrontingStartedEvent(systemId, frontId), cancellationToken);
    }

    public static async ValueTask PublishStateChangedAndEndedAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId systemId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        await eventBus.PublishStateChangedAsync(systemId, cancellationToken);
        await eventBus.PublishAsync(new FrontingEndedEvent(systemId, alterId), cancellationToken);
    }

    public static async ValueTask PublishStateChangedAndSetAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId systemId,
        FrontId frontId,
        CancellationToken cancellationToken = default)
    {
        await eventBus.PublishStateChangedAsync(systemId, cancellationToken);
        await eventBus.PublishAsync(new FrontingSetEvent(systemId, frontId), cancellationToken);
    }

    public static async ValueTask PublishStateChangedAndBulkUpdatedAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId systemId,
        CancellationToken cancellationToken = default)
    {
        await eventBus.PublishStateChangedAsync(systemId, cancellationToken);
        await eventBus.PublishAsync(new FrontingBulkUpdatedEvent(systemId), cancellationToken);
    }

    public static async ValueTask PublishStateChangedAndCommentUpdatedAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId systemId,
        FrontId frontId,
        CancellationToken cancellationToken = default)
    {
        await eventBus.PublishStateChangedAsync(systemId, cancellationToken);
        await eventBus.PublishAsync(new FrontCommentUpdatedEvent(systemId, frontId), cancellationToken);
    }

    public static async ValueTask PublishStateChangedAndPrimaryChangedAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId systemId,
        AlterId? alterId,
        CancellationToken cancellationToken = default)
    {
        await eventBus.PublishStateChangedAsync(systemId, cancellationToken);
        await eventBus.PublishAsync(new FrontingPrimaryChangedEvent(systemId, alterId), cancellationToken);
    }

    /// <summary>
    /// Publishes <see cref="FrontDeletedEvent"/>, prefixed by
    /// <see cref="FrontingStateChangedEvent"/> only when the deleted row was the active
    /// one (deleting a history row doesn't change the current state).
    /// </summary>
    public static async ValueTask PublishDeletedAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId systemId,
        FrontId frontId,
        bool wasActive,
        CancellationToken cancellationToken = default)
    {
        if (wasActive)
            await eventBus.PublishStateChangedAsync(systemId, cancellationToken);

        await eventBus.PublishAsync(new FrontDeletedEvent(systemId, frontId), cancellationToken);
    }

    /// <summary>
    /// Fans one <see cref="FrontingEndedEvent"/> per alter id, sequentially. Sequential
    /// ordering matches every handler here — no parallelism is asked for and the bus's
    /// subscribers assume in-order delivery per publisher.
    /// </summary>
    public static async ValueTask PublishEndedForAltersAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId systemId,
        IEnumerable<AlterId> alterIds,
        CancellationToken cancellationToken = default)
    {
        foreach (var alterId in alterIds)
        {
            await eventBus.PublishAsync(new FrontingEndedEvent(systemId, alterId), cancellationToken);
        }
    }

    /// <summary>
    /// Emits a "primary cleared" <see cref="FrontingPrimaryChangedEvent"/> only when a
    /// primary was actually present before the mutation. Callers can invoke this
    /// unconditionally and let the flag be the guard.
    /// </summary>
    public static ValueTask PublishPrimaryClearedIfNeededAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId systemId,
        bool wasPrimaryPresent,
        CancellationToken cancellationToken = default)
        => wasPrimaryPresent
            ? eventBus.PublishAsync(new FrontingPrimaryChangedEvent(systemId, null), cancellationToken)
            : ValueTask.CompletedTask;
}
