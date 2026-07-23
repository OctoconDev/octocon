using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Shared.Domain.Fronting;

// Intent-named publish helpers on IClusterEventBus for fronting events. Each method
// emits FrontingStateChangedEvent first (cache invalidation), then the transition event
// (specific reaction). Publish-only — orchestration lives in FrontingCommandFlow.
internal static class FrontingEventBusExtensions
{
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

    /// <summary>Publishes FrontDeletedEvent; prefixes with FrontingStateChangedEvent only
    /// when the deleted row was the active one.</summary>
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

    /// <summary>Fans one FrontingEndedEvent per alter id, sequentially (subscribers
    /// assume in-order delivery per publisher).</summary>
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

    /// <summary>"Primary cleared" event guarded by <paramref name="wasPrimaryPresent"/>
    /// so callers can invoke unconditionally.</summary>
    public static ValueTask PublishPrimaryClearedIfNeededAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId systemId,
        bool wasPrimaryPresent,
        CancellationToken cancellationToken = default)
        => wasPrimaryPresent
            ? eventBus.PublishAsync(new FrontingPrimaryChangedEvent(systemId, null), cancellationToken)
            : ValueTask.CompletedTask;
}
