using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts.Events;

public sealed record FriendshipAddedEvent(ScopedSystemId TargetSystemId, SystemId SystemId) : ITargetedClusterEvent;

public sealed record FriendshipRemovedEvent(ScopedSystemId TargetSystemId, SystemId SystemId) : ITargetedClusterEvent;

public sealed record FriendshipTrustedEvent(ScopedSystemId TargetSystemId, SystemId SystemId) : ITargetedClusterEvent;

public sealed record FriendshipUntrustedEvent(ScopedSystemId TargetSystemId, SystemId SystemId) : ITargetedClusterEvent;

public sealed record FriendRequestSentEvent(ScopedSystemId TargetSystemId, SystemId ToSystemId) : ITargetedClusterEvent;

public sealed record FriendRequestReceivedEvent(ScopedSystemId TargetSystemId, SystemId FromSystemId) : ITargetedClusterEvent;

public sealed record FriendRequestRemovedFromEvent(ScopedSystemId TargetSystemId, SystemId FromSystemId) : ITargetedClusterEvent;

public sealed record FriendRequestRemovedToEvent(ScopedSystemId TargetSystemId, SystemId ToSystemId) : ITargetedClusterEvent;
