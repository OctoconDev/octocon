using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Read;

namespace Interfold.Contracts;

public sealed record FriendRequestSocketPayload(FriendshipRequestModel Request, FriendProfileReadModel System) : ISocketPayload;

public sealed record FriendIdSocketPayload(SystemId FriendId) : ISocketPayload;
