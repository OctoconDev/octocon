namespace Interfold.Contracts;

public sealed record SocketJoinReconnectPayload(SocketSelfReadModel System) : ISocketPayload;
