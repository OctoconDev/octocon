using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts.Events;

/// <summary>
/// Marker for cluster events whose primary recipient is a single system identified by
/// <see cref="TargetSystemId"/>.
/// <para>
/// <c>IClusterEventBus</c> uses this contract to filter delivery: a subscription created
/// with a non-null <c>targetSystemId</c> only receives events whose <see cref="TargetSystemId"/>
/// matches that value. Events that do not implement this interface are broadcast to every
/// subscriber regardless of scoping, which preserves correctness for any future non-targeted signals.
/// </para>
/// <para>
/// <see cref="TargetSystemId"/> is a <see cref="ScopedSystemId"/> so
/// the event bus's equality filter compares two wire-canonical strings by construction
/// rather than relying on every publisher hand-formatting a scoped prefix correctly.
/// </para>
/// </summary>
public interface ITargetedClusterEvent
{
    ScopedSystemId TargetSystemId { get; }
}
