using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Events;

public sealed record AlterCreatedEvent(ScopedSystemId TargetSystemId, AlterId AlterId) : ITargetedClusterEvent;

public sealed record AlterUpdatedEvent(ScopedSystemId TargetSystemId, AlterId AlterId) : ITargetedClusterEvent;

public sealed record AlterDeletedEvent(ScopedSystemId TargetSystemId, AlterId AlterId) : ITargetedClusterEvent;
