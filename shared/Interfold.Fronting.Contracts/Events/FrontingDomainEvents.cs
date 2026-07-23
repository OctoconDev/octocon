using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts.Events;

/// <summary>Published to <c>IClusterEventBus</c> on any fronting-state change. Consumed on
/// primary nodes by <c>FrontNotifierBackgroundService</c> for FCM fan-out (Phase N).</summary>
public sealed record FrontingStateChangedEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record FrontDeletedEvent(ScopedSystemId TargetSystemId, FrontId FrontId) : ITargetedClusterEvent;

/// <summary>Emits socket <c>"fronting_started"</c>.</summary>
public sealed record FrontingStartedEvent(ScopedSystemId TargetSystemId, FrontId FrontId) : ITargetedClusterEvent;

/// <summary>Emits socket <c>"fronting_ended"</c>.</summary>
public sealed record FrontingEndedEvent(ScopedSystemId TargetSystemId, AlterId AlterId) : ITargetedClusterEvent;

/// <summary>Emits socket <c>"fronting_set"</c>.</summary>
public sealed record FrontingSetEvent(ScopedSystemId TargetSystemId, FrontId FrontId) : ITargetedClusterEvent;

/// <summary>Emits socket <c>"fronting_bulk"</c>.</summary>
public sealed record FrontingBulkUpdatedEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;

/// <summary>Emits socket <c>"front_updated"</c> after a front comment change.</summary>
public sealed record FrontCommentUpdatedEvent(ScopedSystemId TargetSystemId, FrontId FrontId) : ITargetedClusterEvent;

/// <summary>Emits socket <c>"primary_front"</c>.</summary>
public sealed record FrontingPrimaryChangedEvent(ScopedSystemId TargetSystemId, AlterId? AlterId) : ITargetedClusterEvent;
