using Interfold.Shared.Contracts.Ids;

namespace Interfold.Friendships.Contracts.Models.Commands;

public sealed record RemoveFriendshipCommand(SystemId FriendSystemId);

public sealed record SetFriendTrustCommand(SystemId FriendSystemId, bool Trusted);

// TargetSystemId's name is frozen (persisted payload + idempotency hashes); the type is
// FriendLookup because the route accepts either a system id (bare or 'id:'-prefixed) or a
// username ('username:'-prefixed) — everything else fails route binding with a 400.
public sealed record SendFriendRequestCommand(FriendLookup TargetSystemId);

public sealed record AcceptFriendRequestCommand(SystemId SourceSystemId);

public sealed record RejectFriendRequestCommand(SystemId SourceSystemId);

public sealed record CancelFriendRequestCommand(SystemId TargetSystemId);
