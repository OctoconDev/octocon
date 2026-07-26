namespace Interfold.Socket.Contracts;

public sealed record SocketJoinReconnectPayload(SocketSelfReadModel System) : ISocketPayload;
