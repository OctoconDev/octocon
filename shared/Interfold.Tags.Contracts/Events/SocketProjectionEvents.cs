using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;
using Interfold.Tags.Contracts.Ids;

namespace Interfold.Tags.Contracts.Events;

// Extracted from Interfold.Shared.Contracts/Events/SocketProjectionEvents.cs during Phase-3 Tags
// migration. Other feature-scoped events (Alter*, Poll*, Journal*, Friendship*, Settings*)
// migrate with their respective features.

public sealed record TagCreatedEvent(ScopedSystemId TargetSystemId, TagId TagId) : ITargetedClusterEvent;

public sealed record TagUpdatedEvent(ScopedSystemId TargetSystemId, TagId TagId) : ITargetedClusterEvent;

public sealed record TagDeletedEvent(ScopedSystemId TargetSystemId, TagId TagId) : ITargetedClusterEvent;
