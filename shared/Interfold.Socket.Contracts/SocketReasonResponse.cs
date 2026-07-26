using Interfold.Shared.Contracts;

namespace Interfold.Socket.Contracts;

public sealed record SocketReasonResponse(ErrorCode Reason) : ISocketPayload;
