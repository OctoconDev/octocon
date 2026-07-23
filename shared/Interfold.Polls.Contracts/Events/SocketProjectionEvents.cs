using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts.Events;

public sealed record PollCreatedEvent(ScopedSystemId TargetSystemId, PollId PollId) : ITargetedClusterEvent;

public sealed record PollUpdatedEvent(ScopedSystemId TargetSystemId, PollId PollId) : ITargetedClusterEvent;

public sealed record PollDeletedEvent(ScopedSystemId TargetSystemId, PollId PollId) : ITargetedClusterEvent;
