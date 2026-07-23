using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Events;

public sealed record PollCreatedEvent(ScopedSystemId TargetSystemId, PollId PollId) : ITargetedClusterEvent;

public sealed record PollUpdatedEvent(ScopedSystemId TargetSystemId, PollId PollId) : ITargetedClusterEvent;

public sealed record PollDeletedEvent(ScopedSystemId TargetSystemId, PollId PollId) : ITargetedClusterEvent;
