using Interfold.Friendships.Contracts.Models.Read;
using Interfold.Shared.Contracts.Ids;
using Interfold.Socket.Contracts;

namespace Interfold.Friendships.Contracts;

public sealed record FriendRequestSocketPayload(FriendshipRequestModel Request, FriendProfileReadModel System) : ISocketPayload;

public sealed record FriendIdSocketPayload(SystemId FriendId) : ISocketPayload;
