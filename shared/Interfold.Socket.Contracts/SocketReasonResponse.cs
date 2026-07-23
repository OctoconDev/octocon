using Interfold.Shared.Contracts.Enums;

namespace Interfold.Shared.Contracts;

public sealed record SocketReasonResponse(ErrorCode Reason) : ISocketPayload;
