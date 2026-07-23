namespace Interfold.Shared.Contracts;

public sealed record SocketJoinReconnectPayload(SocketSelfReadModel System) : ISocketPayload;
