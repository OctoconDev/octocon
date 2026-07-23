using Interfold.Contracts.Enums;

namespace Interfold.Contracts;

public sealed record SocketReasonResponse(ErrorCode Reason) : ISocketPayload;
