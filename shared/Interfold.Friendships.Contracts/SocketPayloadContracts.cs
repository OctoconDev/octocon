using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;

namespace Interfold.Shared.Contracts;

public sealed record FriendRequestSocketPayload(FriendshipRequestModel Request, FriendProfileReadModel System) : ISocketPayload;

public sealed record FriendIdSocketPayload(SystemId FriendId) : ISocketPayload;
