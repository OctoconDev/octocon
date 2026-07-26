using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Alters.Contracts.Events;

public sealed record AlterCreatedEvent(ScopedSystemId TargetSystemId, AlterId AlterId) : ITargetedClusterEvent;

public sealed record AlterUpdatedEvent(ScopedSystemId TargetSystemId, AlterId AlterId) : ITargetedClusterEvent;

public sealed record AlterDeletedEvent(ScopedSystemId TargetSystemId, AlterId AlterId) : ITargetedClusterEvent;
